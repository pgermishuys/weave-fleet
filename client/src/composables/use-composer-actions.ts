import { computed, shallowRef } from "vue";
import { storeToRefs } from "pinia";
import { useAbortSession } from "@/composables/use-session-actions";
import { useDraftState } from "@/composables/use-draft-state";
import { useHarnesses } from "@/composables/use-harnesses";
import { useRunShellCommand } from "@/composables/use-run-shell-command";
import { useSendCommand } from "@/composables/use-send-command";
import { useSendPrompt } from "@/composables/use-send-prompt";
import { useSessionQueue, type QueuedMessage, type QueueOptions } from "@/composables/use-session-queue";
import { useSideConversation } from "@/composables/use-side-conversation";
import { modelFromKey } from "@/lib/agent-model-choice";
import type { ImageAttachment } from "@/lib/client-types";
import { isShellDraft, shellDraftCommand } from "@/lib/shell-commands";
import { parseSideQuestion } from "@/lib/side-conversation";
import { parseSlashCommand } from "@/lib/slash-command-utils";
import { trackAction } from "@/lib/track-action";
import { useSessionsStore } from "@/stores/sessions";

/**
 * What a composer does with a draft, shared by the desktop Composer and the phone's: whether the session is busy,
 * what its harness can do, and where a draft goes (sent, queued, steered into the turn, run as a shell command, asked
 * on the side). The pure decisions are exported on their own so both composers apply the same rules.
 */

export type ComposerStatus = "idle" | "busy" | "waiting_input";

/** The composer's view of the session: busy while a turn runs (or right after sending), waiting when it asks. */
export function composerStatus(activityStatus: string | null | undefined, optimisticBusy = false): ComposerStatus {
  if (optimisticBusy) return "busy";
  if (activityStatus === "busy" || activityStatus === "delegating" || activityStatus === "retry") return "busy";
  if (activityStatus === "waiting_input") return "waiting_input";
  return "idle";
}

export interface HarnessCapabilities {
  /** It takes a message into a running turn, read at the agent's next step. */
  canSteer: boolean;
  /** Enter steers and Queue waits (OpenCode 2); other harnesses that steer keep Enter for the queue. */
  steersByDefault: boolean;
  /** `!command` runs in the session's folder. */
  supportsShell: boolean;
  /** `/btw` side conversations. */
  supportsSide: boolean;
}

interface HarnessLike {
  type: string;
  capabilities: { supportsSteering?: boolean; supportsShellCommands?: boolean; supportsSideConversations?: boolean };
}

export function harnessCapabilities(harnessType: string | null | undefined, harnesses: readonly HarnessLike[]): HarnessCapabilities {
  const type = harnessType ?? "opencode";
  const caps = harnesses.find((harness) => harness.type === type)?.capabilities;
  const canSteer = caps?.supportsSteering === true;
  return {
    canSteer,
    steersByDefault: canSteer && type === "opencode2",
    supportsShell: caps?.supportsShellCommands === true,
    supportsSide: caps?.supportsSideConversations === true,
  };
}

/** Where a draft goes. */
export type DraftRoute =
  | { kind: "side"; question: string }
  | { kind: "shell"; command: string; queue: boolean }
  | { kind: "command"; command: string; args: string; queue: boolean }
  | { kind: "prompt"; queue: boolean; steer: boolean }
  | { kind: "empty" };

export interface DraftContext {
  status: ComposerStatus;
  /** Send now: into the running turn, when the harness can take it there. */
  steer: boolean;
  caps: HarnessCapabilities;
  /** The side conversation is open: everything typed goes to it. */
  sideOpen: boolean;
}

/**
 * Where `text` goes: `/btw` (or anything while the side conversation is open) to the side; `!` to the shell, queued
 * while a turn runs; a slash command as a command, queued while a turn runs; anything else as a prompt, queued while a
 * turn runs unless it's steered in and the harness can take it.
 */
export function routeDraft(text: string, context: DraftContext): DraftRoute {
  const trimmed = text.trim();
  if (!trimmed) return { kind: "empty" };

  const side = parseSideQuestion(trimmed);
  if (side !== null || context.sideOpen) return { kind: "side", question: side ?? trimmed };

  const busy = context.status === "busy";
  if (context.caps.supportsShell && isShellDraft(text)) {
    return { kind: "shell", command: shellDraftCommand(text), queue: busy };
  }

  const command = parseSlashCommand(text);
  if (command) return { kind: "command", command: command.command, args: command.args, queue: busy };

  const steer = context.steer && busy && context.caps.canSteer;
  return { kind: "prompt", queue: busy && !steer, steer };
}

/** The phone's one button: Send when idle, Queue while working, Stop while working with nothing typed. */
export function primaryAction(status: ComposerStatus, hasContent: boolean): "send" | "queue" | "stop" {
  if (status !== "busy") return "send";
  return hasContent ? "queue" : "stop";
}

/** A queued item that can go now: anything when idle; while a turn runs, only a message, where the harness steers. */
export function canSendQueuedNow(item: QueuedMessage, status: ComposerStatus, caps: HarnessCapabilities): boolean {
  if (status === "idle") return true;
  return status === "busy" && caps.canSteer && item.kind === "prompt";
}

/**
 * The actions behind a composer for one session, built on the same composables the desktop Composer uses. The draft is
 * the session's shared draft (`useDraftState`), so what's typed on the phone is what the desktop would send.
 */
export function useComposerActions(sessionId: string) {
  const sessionsStore = useSessionsStore();
  const { sessions, sessionStateOverrides } = storeToRefs(sessionsStore);
  const { harnesses } = useHarnesses();
  const { draft, setText } = useDraftState(sessionId, { agentId: "", modelId: "" });
  const { canSend, error: promptError, sendPrompt } = useSendPrompt(sessionId);
  const { error: commandError, sendCommand } = useSendCommand(sessionId);
  const { error: shellError, runShellCommand } = useRunShellCommand(sessionId);
  const { queue, error: queueError, enqueue, remove, sendNow } = useSessionQueue(sessionId);
  const { abortSession, isAborting } = useAbortSession();
  const side = useSideConversation(() => sessionId);
  const optimisticBusy = shallowRef(false);

  const session = computed(() => sessions.value.find((item) => item.session.id === sessionId) ?? null);
  const activity = computed(() => sessionStateOverrides.value[sessionId]?.activityStatus ?? session.value?.activityStatus);
  const status = computed(() => composerStatus(activity.value, optimisticBusy.value));
  const caps = computed(() => harnessCapabilities(session.value?.harnessType, harnesses.value as readonly HarnessLike[]));
  const archived = computed(() => (sessionStateOverrides.value[sessionId]?.retentionStatus ?? session.value?.retentionStatus) === "archived");
  const disabled = computed(() => archived.value || !canSend.value);
  const error = computed(() => side.error.value ?? queueError.value ?? shellError.value ?? commandError.value ?? promptError.value);

  // Optimistic busy lasts until the session says what it's doing.
  let optimisticTimer: ReturnType<typeof setTimeout> | null = null;
  function markBusy(): void {
    optimisticBusy.value = true;
    if (optimisticTimer) clearTimeout(optimisticTimer);
    optimisticTimer = setTimeout(() => {
      optimisticBusy.value = false;
    }, 4000);
  }

  function choices(): Pick<QueueOptions, "agent" | "model" | "effort"> {
    return {
      agent: draft.agentId || undefined,
      model: modelFromKey(draft.modelId) ?? undefined,
      effort: draft.effort !== "medium" ? draft.effort : undefined,
    };
  }

  /** Queues `text`; the draft clears at once and comes back if Fleet refuses it, unless something new was typed. */
  function queueText(typed: string, text: string, options: QueueOptions): void {
    setText("");
    void enqueue(text, options).then((queued) => {
      if (!queued && draft.text.length === 0) setText(typed);
    });
  }

  /** Sends the draft where `routeDraft` says. Returns what it did. */
  function submit(options: { steer?: boolean; attachments?: ImageAttachment[] } = {}): DraftRoute {
    if (disabled.value) return { kind: "empty" };
    const typed = draft.text;
    const route = routeDraft(typed, { status: status.value, steer: options.steer === true, caps: caps.value, sideOpen: side.isOpen.value });
    if (route.kind === "empty" && !options.attachments?.length) return route;

    switch (route.kind) {
      case "side":
        trackAction("session.side_question", sessionId);
        setText("");
        void side.ask(route.question, choices()).then((asked) => {
          if (!asked && draft.text.length === 0) setText(typed);
        });
        break;
      case "shell":
        trackAction("session.shell", sessionId);
        if (route.queue) {
          queueText(typed, typed.trim(), { kind: "shell" });
        } else {
          setText("");
          void runShellCommand(route.command).then((ran) => {
            if (!ran && draft.text.length === 0) setText(typed);
          });
        }
        break;
      case "command":
        if (route.queue) queueText(typed, typed.trim(), { kind: "command", command: route.command, arguments: route.args || undefined, ...choices() });
        else if (sendCommand(route.command, route.args)) markBusy();
        break;
      default:
        if (route.kind === "prompt" && route.queue) {
          queueText(typed, typed.trim(), { kind: "prompt", ...choices() });
          break;
        }
        if (sendPrompt(options.attachments?.length ? options.attachments : undefined, undefined, route.kind === "prompt" && route.steer ? { steer: true } : undefined)) {
          markBusy();
          trackAction(route.kind === "prompt" && route.steer ? "session.prompt.steer" : "session.prompt", sessionId);
        }
    }
    return route;
  }

  async function stop(): Promise<void> {
    if (status.value !== "busy" || isAborting.value) return;
    try {
      await abortSession(sessionId);
      optimisticBusy.value = false;
      sessionsStore.patchSession(sessionId, { activityStatus: "idle", sessionStatus: "idle" });
    } catch {
      // The mutation keeps its error.
    }
  }

  function sendQueued(item: QueuedMessage): void {
    if (!canSendQueuedNow(item, status.value, caps.value)) return;
    void sendNow(item.id);
  }

  return {
    draft,
    setText,
    status,
    caps,
    disabled,
    error,
    queue,
    removeQueued: (item: QueuedMessage) => remove(item.id),
    sendQueued,
    canSendQueued: (item: QueuedMessage) => canSendQueuedNow(item, status.value, caps.value),
    submit,
    stop,
    isStopping: isAborting,
    side,
  };
}
