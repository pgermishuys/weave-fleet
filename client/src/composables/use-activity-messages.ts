import { computed, type Ref } from "vue";
import { storeToRefs } from "pinia";
import { parsePeerMessage, parsePeerUpdate, type PeerOutcome, type PeerSender } from "@/lib/session-messages";
import { parseSessionReferences, type SessionReference } from "@/lib/session-references";
import { parseBackgroundNotice, type BackgroundNotice, type BackgroundState } from "@/lib/background-work";
import type { RunningWorkItem } from "@/lib/running-work";
import { compactionOf, foldCompactionSummaries, type CompactionView } from "@/lib/compaction";
import { modelDisplayName } from "@/lib/agent-model-choice";
import { isDelegationWaiting } from "@/lib/domain-event-reducer";
import { getTool } from "@/lib/tools";
import {
  isSubagentTool,
  subagentKind,
  subagentTask,
  toToolCardItem,
  withoutRedrawnPages,
  type ToolCardItem,
} from "@/components/session/activity-stream-tool-card";
import type {
  AccumulatedFilePart,
  AccumulatedMessage,
  AccumulatedReasoningPart,
  AccumulatedToolPart,
  DelegationDto,
} from "@/lib/client-types";
import type { SlashCommand, TurnError } from "@/lib/domain-events";
import { isQuestionPart } from "@/lib/question-types";
import { mergeMessagesByTimestamp } from "@/lib/merge-messages";
import { workflowMessageKey, workflowMessageLabel } from "@/lib/workflows";
import { toShellCommandView, type ShellCommandView } from "@/lib/shell-commands";
import type { MachineTarget } from "@/lib/machine-target";
import {
  getClusterPosition,
  getDisplayAuthor,
  getSenderKey,
  isSameSender,
  messageBody,
  sameItems,
  type ClusterPosition,
} from "@/lib/activity-messages";
import { useBackgroundWork } from "@/composables/use-background-work";
import type { ModelOption } from "@/composables/use-models";
import type { SentPromptMessage } from "@/composables/use-send-prompt";
import { useSessionsStore } from "@/stores/sessions";
import { useWorkflowsStore } from "@/stores/workflows";
import { useBuiltInSkillsStore } from "@/stores/built-in-skills";
import { useThemeStore } from "@/stores/theme";

export interface ImageAttachmentDisplay {
  url: string;
  filename: string;
}

/** A message as the conversation draws it. */
export interface ActivityMessage {
  id: string;
  author: string;
  modelName?: string;
  senderKey: string;
  role: AccumulatedMessage["role"];
  createdAt?: number;
  body: string;
  images: ImageAttachmentDisplay[];
  tools?: ToolCardItem[];
  questionParts?: AccumulatedToolPart[];
  reasoningParts?: AccumulatedReasoningPart[];
  optimisticStatus?: "pending" | "confirmed" | "needs_retry";
  clusterPosition: ClusterPosition;
  showIdentity: boolean;
  /** Set when the turn that produced this message failed. */
  turnError?: TurnError;
  /** Set when another Fleet session sent this message with fleet_message, or Fleet sent an update about one. */
  peer?: PeerSender;
  /** Set when this is Fleet's update that a session this one messaged is done. */
  peerOutcome?: PeerOutcome;
  /** Set when this is the notice that work the agent moved into the background finished. */
  background?: BackgroundNotice;
  /**
   * Set on what Fleet sent into a workflow step's session: "Workflow · step 2 of 6" (and "· you finish this step") on
   * the prompt the step started with, "Fleet · you pressed Move on" on the wrap-up.
   */
  workflowStep?: string;
  /** Set on a prompt the user sent into a running turn (steered): the agent read it at its next step. */
  steered?: boolean;
  /** Sessions the user referenced with `@` in this message: their tokens show as chips. */
  sessionReferences?: SessionReference[];
  /** Set on a shell command the user ran from the composer: the command and what it printed. */
  shell?: ShellCommandView;
  /** The slash command a message of yours came from; its body is then what the harness made of the command. */
  command?: SlashCommand;
  /** Set when this is where the harness compacted the conversation: it shows as a divider. */
  compaction?: CompactionView;
}

export interface ActivityMessagesOptions {
  sessionId: () => string;
  /** The session's messages, as far as this conversation shows them. */
  sessionMessages: Readonly<Ref<readonly AccumulatedMessage[]>>;
  delegations: Readonly<Ref<readonly DelegationDto[]>>;
  /** What the stream says of the session's work: running, and what ended lately. */
  runningWork: Readonly<Ref<readonly RunningWorkItem[]>>;
  /** What the user sent that the stream hasn't echoed back yet. */
  sentPrompts: Readonly<Ref<readonly SentPromptMessage[]>>;
  /** The catalog that names the model ids messages carry. */
  models: Readonly<Ref<readonly ModelOption[]>>;
  /** The session's machine, which older background work is asked of. */
  machine: MachineTarget;
}

/** The context a tool-item decorator reads besides the call's own card. */
interface ToolDecoratorContext {
  delegations: readonly DelegationDto[];
  parentSessionId: string;
  childInstanceId: (childSessionId: string) => string;
  isBuiltInSkill: (name: string) => boolean;
}

type ToolItemDecorator = (item: ToolCardItem, part: AccumulatedToolPart, context: ToolDecoratorContext) => ToolCardItem;

/** Points a sub-agent call's row at the session it started, once that session exists. */
const withDelegation: ToolItemDecorator = (item, part, context) => {
  if (!isSubagentTool(part.tool)) {
    return item;
  }

  const delegation = context.delegations.find((candidate) =>
    (candidate.parentToolCallId === part.callId || candidate.parentToolCallId === part.partId)
    && candidate.childSessionId);
  if (!delegation?.childSessionId) {
    return item;
  }

  const childInstanceId = context.childInstanceId(delegation.childSessionId);
  return {
    ...item,
    delegation: {
      href: `/sessions/${delegation.childSessionId}?instanceId=${childInstanceId}&parentSessionId=${context.parentSessionId}`,
      childSessionId: delegation.childSessionId,
      childInstanceId,
      parentSessionId: context.parentSessionId,
      agent: subagentKind(part),
      task: subagentTask(part) || delegation.title,
      status: delegation.status,
      needsInput: isDelegationWaiting(delegation),
      background: delegation.background === true,
    },
  };
};

/** A call that loaded one of Fleet's built-in skills offers Improve on its row. */
const withImprove: ToolItemDecorator = (item, part, context) =>
  getTool(part.tool).category === "skill" && context.isBuiltInSkill(item.title) ? { ...item, improvable: true } : item;

/** What each tool card gets added to it, in order. */
const toolItemDecorators: readonly ToolItemDecorator[] = [withDelegation, withImprove];

interface DerivedMessage {
  /** Everything besides the message itself that the derived message was built from. */
  inputs: readonly unknown[];
  message: ActivityMessage;
}

/**
 * A conversation's message views: the stream's messages (and what the user sent that it hasn't echoed yet) as the
 * conversation draws them, clustered by sender. A streamed token rebuilds only the view of the message it belongs to;
 * every other message keeps its view, so its bubble doesn't render again.
 */
export function useActivityMessages(options: ActivityMessagesOptions) {
  const { sessionId, sessionMessages, delegations, runningWork, sentPrompts, models, machine } = options;
  const sessionsStore = useSessionsStore();
  const workflowsStore = useWorkflowsStore();
  const builtInSkills = useBuiltInSkillsStore();
  const themeStore = useThemeStore();
  const { sessions } = storeToRefs(sessionsStore);

  /** The run this session is a step of, which says which of its messages Fleet sent. */
  const workflowRun = computed(() => workflowsStore.runForSession(sessionId()));

  const { workByCall, finishedBackground } = useBackgroundWork({ sessionId, messages: sessionMessages, runningWork, machine });

  const derivedMessages = new WeakMap<AccumulatedMessage, DerivedMessage>();

  /** What a message's view reads besides the message: only what its own parts need, so the rest can't invalidate it. */
  function derivationInputs(message: AccumulatedMessage, finished: ReadonlyMap<string, BackgroundState>): unknown[] {
    const inputs: unknown[] = [];
    if (message.role === "user") {
      inputs.push(workflowMessageKey(workflowRun.value, sessionId()));
    }
    if (message.modelID) {
      inputs.push(models.value);
    }

    const toolParts = message.parts.filter((part): part is AccumulatedToolPart => part.type === "tool");
    if (toolParts.length > 0) {
      inputs.push(finished);
      for (const part of toolParts) inputs.push(workByCall.value.get(part.callId) ?? null);
    }

    // A notice of background work: how it ended can change after it arrives (Fleet stopped it).
    const body = messageBody(message);
    if (body.startsWith("<shell ") || body.startsWith("<subagent ")) {
      inputs.push(finished);
    }

    if (toolParts.some((part) => getTool(part.tool).category === "skill")) {
      inputs.push(builtInSkills.skills);
    }

    if (toolParts.some((part) => isSubagentTool(part.tool))) {
      inputs.push(delegations.value, sessions.value, sessionId());
    }

    return inputs;
  }

  function toToolItem(part: AccumulatedToolPart, finished: ReadonlyMap<string, BackgroundState>): ToolCardItem {
    const context: ToolDecoratorContext = {
      // Read when a decorator needs them, so a message without a sub-agent call doesn't depend on the delegations.
      get delegations() {
        return delegations.value;
      },
      get parentSessionId() {
        return sessionId();
      },
      childInstanceId: (childSessionId) => sessionsStore.sessionById(childSessionId)?.instanceId ?? childSessionId,
      isBuiltInSkill: (name) => builtInSkills.isBuiltIn(name),
    };
    return toolItemDecorators.reduce(
      (item, decorate) => decorate(item, part, context),
      toToolCardItem(part, finished, workByCall.value.get(part.callId)),
    );
  }

  function toActivityMessage(message: AccumulatedMessage, finished: ReadonlyMap<string, BackgroundState>): ActivityMessage {
    if (message.role === "shell") {
      return toShellActivityMessage(message);
    }

    const author = getDisplayAuthor(message);
    const rawBody = messageBody(message);
    // A notice comes from the harness, not the user or the agent: Fleet gives it its own role, which the client
    // reads as an assistant-side message.
    const background = withEnd(parseBackgroundNotice(rawBody), finished);
    const peerMessage = message.role === "user" && !background ? parsePeerMessage(rawBody) : null;
    const peerUpdate = message.role === "user" && !peerMessage && !background ? parsePeerUpdate(rawBody) : null;
    const fromPeer = peerMessage ?? peerUpdate;
    // A message with @ sessions carries Fleet's block for the agent after what the user typed.
    const referenced = message.role === "user" && !fromPeer && !background ? parseSessionReferences(rawBody) : null;

    return {
      id: message.messageId,
      author,
      modelName: modelDisplayName(message.modelID, models.value),
      senderKey: getSenderKey(message.role, message.agent),
      role: message.role,
      createdAt: message.createdAt,
      body: background ? background.text : fromPeer ? fromPeer.text : referenced ? referenced.text : rawBody,
      sessionReferences: referenced?.references.length ? referenced.references : undefined,
      peer: fromPeer?.peer,
      peerOutcome: peerUpdate?.outcome,
      background: background ?? undefined,
      workflowStep: message.role === "user" ? workflowMessageLabel(workflowRun.value, sessionId(), message.messageId, rawBody) ?? undefined : undefined,
      steered: message.role === "user" && message.steered ? true : undefined,
      images: message.parts
        .filter((part): part is AccumulatedFilePart => part.type === "file" && part.mime.startsWith("image/"))
        .map((part) => ({ url: part.url, filename: part.filename?.trim() || "image" })),
      tools: message.parts
        .filter((part): part is AccumulatedToolPart => part.type === "tool" && !isQuestionPart(part as AccumulatedToolPart))
        .map((part) => toToolItem(part, finished)),
      questionParts: message.parts
        .filter((part): part is AccumulatedToolPart => part.type === "tool" && isQuestionPart(part as AccumulatedToolPart)),
      reasoningParts: message.parts
        .filter((part): part is AccumulatedReasoningPart => part.type === "reasoning"),
      clusterPosition: "single" as const,
      showIdentity: true,
      turnError: message.turnError,
      command: message.role === "user" ? message.command : undefined,
      compaction: compactionOf(message),
    } satisfies ActivityMessage;
  }

  /** A message's view, built again only when what it was built from changed. */
  function deriveActivityMessage(message: AccumulatedMessage, finished: ReadonlyMap<string, BackgroundState>): ActivityMessage {
    const inputs = derivationInputs(message, finished);
    const cached = derivedMessages.get(message);
    if (cached && sameItems(cached.inputs, inputs)) {
      return cached.message;
    }

    const derived = toActivityMessage(message, finished);
    derivedMessages.set(message, { inputs, message: derived });
    return derived;
  }

  function hasVisibleMessageContent(message: ActivityMessage): boolean {
    return message.body.trim().length > 0
      // Thinking on its own is a message too, so the folded line follows it while the model is still thinking.
      || (themeStore.thinking !== "hidden"
        && (message.reasoningParts?.some((part) => part.text.trim() || part.summary?.trim()) ?? false))
      || message.images.length > 0
      || (message.tools?.length ?? 0) > 0
      || (message.questionParts?.length ?? 0) > 0
      || message.shell != null
      || message.compaction != null
      || message.background != null
      // A turn can fail before it produces anything; the failure is the content.
      || message.turnError != null;
  }

  /** What the stream delivered, drawn: its messages in the order it has them, minus those with nothing to show. */
  const deliveredMessages = computed<ActivityMessage[]>(() => {
    const finished = finishedBackground.value;
    // A compaction's summary written as a message of its own (OpenCode's) shows behind its divider, not as a reply.
    const compactions = foldCompactionSummaries(sessionMessages.value);
    // Preserve upstream order from sessionMessages (snapshot + live events)
    return withoutRedrawnPages(sessionMessages.value
      .filter((message) => !compactions.hidden.has(message.messageId))
      .map((message) => {
        const derived = deriveActivityMessage(message, finished);
        const summary = compactions.summaries.get(message.messageId);
        return summary && derived.compaction ? { ...derived, compaction: { ...derived.compaction, summary } } : derived;
      })
      .filter((message) => message.role === "user" || hasVisibleMessageContent(message)));
  });

  const optimisticMessages = computed<ActivityMessage[]>(() => {
    return sentPrompts.value.map((prompt): ActivityMessage => ({
      id: `optimistic-${prompt.id}`,
      author: "You",
      modelName: undefined,
      senderKey: "user",
      role: "user",
      createdAt: prompt.createdAt,
      body: prompt.body,
      images: prompt.images,
      tools: [],
      questionParts: [],
      reasoningParts: [],
      optimisticStatus: prompt.status,
      clusterPosition: "single",
      showIdentity: true,
      steered: prompt.steered,
      sessionReferences: prompt.sessionReferences,
    }));
  });

  const clusteredMessages = new WeakMap<ActivityMessage, ActivityMessage>();

  /** Every message to draw, the user's unechoed prompts among them, with where each sits in its run of one sender. */
  const messages = computed<ActivityMessage[]>(() => {
    const deliveredIds = new Set(deliveredMessages.value.map((message) => message.id));
    const pendingOptimisticMessages = optimisticMessages.value.filter((message) => {
      const deliveredId = message.id.startsWith("optimistic-")
        ? message.id.slice("optimistic-".length)
        : message.id;
      return !deliveredIds.has(deliveredId);
    });

    // Stable merge by createdAt timestamp
    const baseMessages = mergeMessagesByTimestamp(
      deliveredMessages.value,
      pendingOptimisticMessages,
    );

    return baseMessages.map((message, index) => {
      const previousMessage = baseMessages[index - 1];
      const nextMessage = baseMessages[index + 1];
      const groupedWithPrevious = isSameSender(previousMessage, message);
      const groupedWithNext = isSameSender(message, nextMessage);
      const showIdentity = !groupedWithPrevious;
      const clusterPosition = getClusterPosition(groupedWithPrevious, groupedWithNext);

      const cached = clusteredMessages.get(message);
      if (cached && cached.showIdentity === showIdentity && cached.clusterPosition === clusterPosition) {
        return cached;
      }

      const clustered = { ...message, showIdentity, clusterPosition } satisfies ActivityMessage;
      clusteredMessages.set(message, clustered);
      return clustered;
    });
  });

  return { messages, deliveredMessages };
}

/**
 * A notice with how its work really ended. Work Fleet stopped reads stopped; the error the harness reported for it is
 * the stop's doing (a removed shell is "not found"), not the work's, so it isn't shown.
 */
function withEnd(notice: BackgroundNotice | null, finished: ReadonlyMap<string, BackgroundState>): BackgroundNotice | null {
  const state = notice ? finished.get(notice.id) : undefined;
  if (!notice || !state || state === notice.state) return notice;
  return { ...notice, state, text: notice.state === "error" ? "" : notice.text };
}

/** A shell command the user ran: its own block, on the user's side, not a bubble or a tool card of the agent's. */
function toShellActivityMessage(message: AccumulatedMessage): ActivityMessage {
  return {
    id: message.messageId,
    author: "You",
    senderKey: "shell",
    role: "shell",
    createdAt: message.createdAt,
    body: "",
    images: [],
    tools: [],
    questionParts: [],
    reasoningParts: [],
    clusterPosition: "single",
    showIdentity: true,
    shell: toShellCommandView(message) ?? undefined,
  };
}
