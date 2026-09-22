<script setup lang="ts">
import { computed, nextTick, onMounted, onUnmounted, ref, watch } from "vue";
import { ArrowUpRight, Bot, RotateCw, TerminalSquare, TriangleAlert } from "lucide-vue-next";
import { parsePeerMessage, parsePeerUpdate, type PeerOutcome, type PeerSender } from "@/lib/session-messages";
import { finishedBackgroundWork, parseBackgroundNotice, type BackgroundNotice } from "@/lib/background-work";
import { useRouter } from "@tanstack/vue-router";
import { storeToRefs } from "pinia";
import MessageBubble from "@/components/session/MessageBubble.vue";
import ReasoningBlock from "@/components/session/ReasoningBlock.vue";
import WorkingIndicator from "@/components/session/WorkingIndicator.vue";
import { useSessionStream } from "@/composables/use-session-stream";
import { useModels } from "@/composables/use-models";
import { modelDisplayName } from "@/lib/agent-model-choice";
import { isStreamWorking } from "@/lib/domain-event-reducer";
import { useSidebarMobile } from "@/composables/use-sidebar-mobile";
import { clearSentPrompts, reconcileSentPrompts, useSendPrompt, useSentPrompts } from "@/composables/use-send-prompt";
import { isSubagentTool, subagentKind, subagentTask, toToolCardItem } from "@/components/session/activity-stream-tool-card";
import type { ToolCardItem } from "@/components/session/activity-stream-tool-card";
import type { CommandEventName } from "@/lib/command-events";
import type { AccumulatedMessage, AccumulatedPart, AccumulatedToolPart, AccumulatedFilePart, AccumulatedReasoningPart } from "@/lib/client-types";
import type { TurnError } from "@/lib/domain-events";
import { parseVisualPayload, type VisualPayload } from "@/lib/visual-payload";
import { isQuestionPart } from "@/lib/question-types";
import { diagLog } from "@/lib/message-diagnostics";
import { useSessionsStore } from "@/stores/sessions";
import { dispatchSessionUpsert } from "@/lib/session-sync";
import { useCanvasesStore } from "@/stores/canvases";
import { focusServerCanvas } from "@/composables/use-server-canvases";
import { mergeMessagesByTimestamp } from "@/lib/merge-messages";

interface ImageAttachmentDisplay {
  url: string;
  filename: string;
}

interface ActivityMessage {
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
  clusterPosition: "single" | "first" | "middle" | "last";
  showIdentity: boolean;
  /** Set when the turn that produced this message failed. */
  turnError?: TurnError;
  /** Set when another Fleet session sent this message with fleet_message, or Fleet sent an update about one. */
  peer?: PeerSender;
  /** Set when this is Fleet's update that a session this one messaged is done. */
  peerOutcome?: PeerOutcome;
  /** Set when this is the notice that work the agent moved into the background finished. */
  background?: BackgroundNotice;
}

const props = defineProps<{
  sessionId: string;
}>();

const router = useRouter();
const sessionsStore = useSessionsStore();
const { sessions } = storeToRefs(sessionsStore);
const canvasesStore = useCanvasesStore();
const { showRightPanel } = useSidebarMobile();

const selectedSession = computed(() => {
  return sessions.value.find((session) => session.session.id === props.sessionId) ?? null;
});

const { messages: sessionMessages, delegations, sessionStatus, hasMore, isLoadingOlder, isPartial, loadOlder } = useSessionStream(
  computed(() => props.sessionId),
);
// Names for the model ids the messages carry; the catalog belongs to the session on screen.
const { models } = useModels(() => props.sessionId);
const { sentPrompts } = useSentPrompts(props.sessionId);
const { canSend, retryPrompt } = useSendPrompt(props.sessionId);

/** The prompt a failed turn was answering, which Retry sends again. */
const lastUserPrompt = computed<string | undefined>(() => {
  const lastUser = [...sessionMessages.value].reverse().find((message) => message.role === "user");
  const body = lastUser ? renderMessageBody(lastUser.parts).trim() : "";
  return body.length > 0 ? body : undefined;
});

function handleRetryTurn(): void {
  const prompt = lastUserPrompt.value;
  if (prompt) {
    retryPrompt(prompt);
  }
}
const streamRef = ref<HTMLElement | null>(null);
const showJumpToLatest = ref(false);

const SCROLL_BOTTOM_THRESHOLD = 80;
const SCROLL_TOP_THRESHOLD = 100;

let mutationObserver: MutationObserver | null = null;
let keepPinnedToBottom = true;
let scrollFrame: number | null = null;
let isRestoringScroll = false;
let preUpdateScrollHeight = 0;
let preUpdateScrollTop = 0;
let wasLoadingOlder = false;
const cleanupCallbacks: Array<() => void> = [];

// Track the count of assistant messages with renderable content so we only
// clear optimistic prompts when a NEW assistant response appears — not when
// old assistant messages already exist from previous turns.
let lastRenderableAssistantCount = 0;

watch(
  sessionMessages,
  (nextMessages) => {
    if (selectedSession.value) {
      dispatchSessionUpsert({ ...selectedSession.value });
    }

    reconcileSentPrompts(props.sessionId, nextMessages);

    const renderableAssistantCount = nextMessages.filter(
      (message) => hasRenderableAssistantContent(message),
    ).length;

    if (sentPrompts.value.length === 0) {
      // Keep the baseline count updated even when there are no prompts,
      // so that when the user sends a new prompt we don't falsely detect
      // old assistant messages as "new".
      lastRenderableAssistantCount = renderableAssistantCount;
      return;
    }

    if (renderableAssistantCount > lastRenderableAssistantCount) {
      lastRenderableAssistantCount = renderableAssistantCount;

      // Only clear optimistic prompts if every delivered user message already
      // has its text content.  When messages arrive via SSE the envelope
      // (`message.updated`) can land before the parts (`message.part.updated`),
      // so a delivered user message may temporarily have empty parts.  Clearing
      // the optimistic prompt at that point leaves a blank bubble until the
      // part arrives.  `reconcileSentPrompts` (called above) already guards
      // against text-less user messages and will clear them on a subsequent
      // watch trigger once the parts arrive.
      const allUserMessagesHaveText = nextMessages
        .filter((m) => m.role === "user")
        .every((m) => m.parts.some((p) => p.type === "text" && p.text.trim().length > 0));

      // Also check that optimistic prompts with images have their file parts
      // delivered.  The image file part arrives via a separate
      // `message.part.updated` SSE event that can lag behind the text part.
      // Clearing the optimistic prompt before the file part arrives causes the
      // image to disappear until a page refresh.
      const allImagesDelivered = sentPrompts.value.every((prompt) => {
        if (prompt.images.length === 0) return true;
        const delivered = nextMessages.find((m) => m.messageId === prompt.id);
        if (!delivered) return true; // not yet delivered at all — text guard handles this
        const deliveredImageCount = delivered.parts.filter(
          (p) => p.type === "file" && p.mime.startsWith("image/"),
        ).length;
        return deliveredImageCount >= prompt.images.length;
      });

      if (allUserMessagesHaveText && allImagesDelivered) {
        diagLog("stream.clearPrompts", `new assistant content detected – all user messages have text and images, clearing optimistic prompts`, {
          sessionId: props.sessionId,
          sentPromptsCount: sentPrompts.value.length,
        });
        clearSentPrompts(props.sessionId);
      } else {
        diagLog("stream.clearPrompts", `new assistant content detected but some user messages lack text or images – deferring to reconciliation`, {
          sessionId: props.sessionId,
          sentPromptsCount: sentPrompts.value.length,
        });
      }
    }
  },
  { immediate: true, deep: true },
);

/**
 * How work an agent moved into the background ended, by handle. A backgrounded call's own card can't say: OpenCode 2
 * leaves the call finished and running, and only the notice later in the conversation says the work is done.
 */
const finishedBackground = computed(() =>
  finishedBackgroundWork(sessionMessages.value.map((message) => renderMessageBody(message.parts))),
);

const deliveredMessages = computed<ActivityMessage[]>(() => {
  const finished = finishedBackground.value;
  // Preserve upstream order from sessionMessages (snapshot + live events)
  return sessionMessages.value
    .map((message) => {
      const author = getDisplayAuthor(message);
      const rawBody = renderMessageBody(message.parts);
      // A notice comes from the harness, not the user or the agent: Fleet gives it its own role, which the client
      // reads as an assistant-side message.
      const background = parseBackgroundNotice(rawBody);
      const peerMessage = message.role === "user" && !background ? parsePeerMessage(rawBody) : null;
      const peerUpdate = message.role === "user" && !peerMessage && !background ? parsePeerUpdate(rawBody) : null;
      const fromPeer = peerMessage ?? peerUpdate;

      return {
        id: message.messageId,
        author,
        modelName: modelDisplayName(message.modelID, models.value),
        senderKey: getSenderKey(message.role, message.agent),
        role: message.role,
        createdAt: message.createdAt,
        body: background ? background.text : fromPeer ? fromPeer.text : rawBody,
        peer: fromPeer?.peer,
        peerOutcome: peerUpdate?.outcome,
        background: background ?? undefined,
        images: message.parts
          .filter((part): part is AccumulatedFilePart => part.type === "file" && part.mime.startsWith("image/"))
          .map((part) => ({ url: part.url, filename: part.filename?.trim() || "image" })),
        tools: message.parts
          .filter((part): part is AccumulatedToolPart => part.type === "tool" && !isQuestionPart(part as AccumulatedToolPart))
          .map((part) => withDelegation(toToolCardItem(part, finished), part)),
        questionParts: message.parts
          .filter((part): part is AccumulatedToolPart => part.type === "tool" && isQuestionPart(part as AccumulatedToolPart)),
        reasoningParts: message.parts
          .filter((part): part is AccumulatedReasoningPart => part.type === "reasoning"),
        clusterPosition: "single" as const,
        showIdentity: true,
        turnError: message.turnError,
      } satisfies ActivityMessage;
    })
    .filter((message) => message.role === "user" || hasVisibleMessageContent(message));
});

// The header names the model that answers next. A session that was never given one explicitly has only
// the stream to go on, and the stream is open here — so it puts the last answer's model in the store.
const lastAssistantModelId = computed(() => {
  for (let index = sessionMessages.value.length - 1; index >= 0; index -= 1) {
    const message = sessionMessages.value[index];
    if (message.role === "assistant" && message.modelID) {
      return message.modelID;
    }
  }
  return undefined;
});

watch(
  [() => props.sessionId, lastAssistantModelId],
  ([sessionId, modelId]) => {
    if (modelId) {
      sessionsStore.patchSession(sessionId, { lastAssistantModelId: modelId });
    }
  },
  { immediate: true },
);

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
  }));
});

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

    return {
      ...message,
      showIdentity: !groupedWithPrevious,
      clusterPosition: getClusterPosition(groupedWithPrevious, groupedWithNext),
    } satisfies ActivityMessage;
  });
});

// --- Visual canvases ---
// Every diagram or document the agent renders is listed in the canvas picker.
// One that arrives while this conversation is open also opens as a canvas tab;
// history loaded later (or on first render) never opens tabs on its own.
const mountedAt = Date.now();
const LIVE_TOLERANCE_MS = 5_000;
const autoOpenedToolIds = new Set<string>();

const conversationVisuals = computed(() =>
  deliveredMessages.value.flatMap((message) =>
    (message.tools ?? []).flatMap((tool) => {
      const payload = tool.output ? parseVisualPayload(tool.output) : null;
      return payload ? [{ toolId: tool.id, createdAt: message.createdAt, payload }] : [];
    }),
  ),
);

watch(
  conversationVisuals,
  (visuals) => {
    canvasesStore.setKnownVisuals(props.sessionId, visuals.map((visual) => visual.payload));

    for (const visual of visuals) {
      if (autoOpenedToolIds.has(visual.toolId)) continue;
      autoOpenedToolIds.add(visual.toolId);

      const isLive = (visual.createdAt ?? 0) >= mountedAt - LIVE_TOLERANCE_MS;
      if (isLive) {
        canvasesStore.openVisual(props.sessionId, visual.payload);
      }
    }
  },
  { immediate: true },
);

const isStreaming = computed(() => isStreamWorking(sessionStatus.value));
// The turn's clock starts at your last prompt.
const turnStartedAt = computed(() => {
  for (let index = messages.value.length - 1; index >= 0; index -= 1) {
    const message = messages.value[index];
    if (message.role === "user") {
      return message.createdAt ?? null;
    }
  }
  return null;
});

function isNearBottom(element: HTMLElement): boolean {
  return element.scrollHeight - element.scrollTop - element.clientHeight <= SCROLL_BOTTOM_THRESHOLD;
}

function updatePinnedState(): void {
  const element = streamRef.value;
  if (!element) {
    return;
  }

  keepPinnedToBottom = isNearBottom(element);
  showJumpToLatest.value = !keepPinnedToBottom;

  // Trigger loading older messages when scrolled near the top
  if (!isRestoringScroll && element.scrollTop <= SCROLL_TOP_THRESHOLD && hasMore.value && !isLoadingOlder.value) {
    loadOlder();
  }
}

function scrollToBottom(): void {
  const element = streamRef.value;
  if (!element) {
    return;
  }

  element.scrollTop = element.scrollHeight;
  keepPinnedToBottom = true;
  showJumpToLatest.value = false;
}

function handleJumpToLatest(): void {
  scrollToBottom();
}

function handlePeerLinkClick(event: MouseEvent, peer: PeerSender): void {
  // A modified click opens the sender elsewhere, as a link would.
  if (event.metaKey || event.ctrlKey || event.shiftKey || event.button !== 0) return;
  event.preventDefault();
  void router.navigate({ to: "/sessions/$id", params: { id: peer.sessionId }, search: { instanceId: undefined, parentSessionId: undefined } });
}

// The Turns canvas asks for a round: scroll to where it began and mark it for a moment.
const highlightedMessageId = ref<string | null>(null);
let highlightTimer: ReturnType<typeof setTimeout> | undefined;

function showMessage(messageId: string): void {
  const target = streamRef.value?.querySelector<HTMLElement>(`[data-message-id="${CSS.escape(messageId)}"]`);
  if (!target) {
    return;
  }

  keepPinnedToBottom = false;
  target.scrollIntoView({ block: "center", behavior: "smooth" });
  highlightedMessageId.value = messageId;
  clearTimeout(highlightTimer);
  highlightTimer = setTimeout(() => {
    highlightedMessageId.value = null;
  }, 1600);
}

function scrollToTop(): void {
  const element = streamRef.value;

  if (!element) {
    return;
  }

  element.scrollTo({ top: 0, behavior: "smooth" });
}

function handleCopySessionIdCommand(event: Event): void {
  const customEvent = event as CustomEvent<{ sessionId?: string }>;

  if (customEvent.detail?.sessionId !== props.sessionId) {
    return;
  }

  void navigator.clipboard?.writeText(props.sessionId).catch(() => {});
}

function handleFocusPromptCommand(event: Event): void {
  const customEvent = event as CustomEvent<{ sessionId?: string }>;

  if (customEvent.detail?.sessionId !== props.sessionId) {
    return;
  }

  const promptInput = document.querySelector('[data-testid="prompt-input"]');

  if (promptInput instanceof HTMLTextAreaElement || promptInput instanceof HTMLInputElement) {
    promptInput.focus();
  }
}

function handleExportConversationCommand(event: Event): void {
  const customEvent = event as CustomEvent<{ sessionId?: string }>;

  if (customEvent.detail?.sessionId !== props.sessionId) {
    return;
  }

  const title = (selectedSession.value?.session.title ?? props.sessionId)
    .replace(/[^a-z0-9-_]+/gi, "-")
    .replace(/^-+|-+$/g, "");
  const payload = {
    sessionId: props.sessionId,
    title: selectedSession.value?.session.title ?? props.sessionId,
    exportedAt: new Date().toISOString(),
    messages: sessionMessages.value,
  };
  const blob = new Blob([JSON.stringify(payload, null, 2)], { type: "application/json" });
  const url = URL.createObjectURL(blob);
  const anchor = document.createElement("a");

  anchor.href = url;
  anchor.download = `${title || props.sessionId}-conversation.json`;
  document.body.append(anchor);
  anchor.click();
  anchor.remove();
  URL.revokeObjectURL(url);
}

function registerWindowCommandListener(
  eventName: CommandEventName,
  handler: (event: Event) => void,
): () => void {
  window.addEventListener(eventName, handler);

  return () => {
    window.removeEventListener(eventName, handler);
  };
}

function scheduleScrollToBottom(): void {
  if (!keepPinnedToBottom || scrollFrame !== null) {
    return;
  }

  scrollFrame = window.requestAnimationFrame(() => {
    scrollFrame = null;
    scrollToBottom();
  });
}

onMounted(() => {
  nextTick(() => {
    scrollToBottom();
  });

  mutationObserver = new MutationObserver(() => {
    scheduleScrollToBottom();
  });

  if (streamRef.value) {
    mutationObserver.observe(streamRef.value, {
      childList: true,
      subtree: true,
      characterData: true,
    });
  }

  cleanupCallbacks.push(registerWindowCommandListener("weave:command-scroll-top", (event: Event) => {
    const customEvent = event as CustomEvent<{ sessionId?: string }>;

    if (customEvent.detail?.sessionId !== props.sessionId) {
      return;
    }

    scrollToTop();
  }));
  cleanupCallbacks.push(registerWindowCommandListener("weave:command-scroll-bottom", ((event: Event) => {
    const customEvent = event as CustomEvent<{ sessionId?: string }>;

    if (customEvent.detail?.sessionId !== props.sessionId) {
      return;
    }

    scrollToBottom();
  })));
  cleanupCallbacks.push(registerWindowCommandListener("weave:command-show-message", ((event: Event) => {
    const customEvent = event as CustomEvent<{ sessionId?: string; messageId?: string }>;

    if (customEvent.detail?.sessionId !== props.sessionId || !customEvent.detail?.messageId) {
      return;
    }

    void nextTick(() => showMessage(customEvent.detail!.messageId!));
  })));
  cleanupCallbacks.push(registerWindowCommandListener("weave:command-focus-prompt", handleFocusPromptCommand));
  cleanupCallbacks.push(registerWindowCommandListener("weave:command-copy-session-id", handleCopySessionIdCommand));
  cleanupCallbacks.push(registerWindowCommandListener("weave:command-export-conversation", handleExportConversationCommand));
});

onUnmounted(() => {
  mutationObserver?.disconnect();
  mutationObserver = null;
  clearTimeout(highlightTimer);

  for (const cleanup of cleanupCallbacks.splice(0)) {
    cleanup();
  }

  if (scrollFrame !== null) {
    window.cancelAnimationFrame(scrollFrame);
    scrollFrame = null;
  }
});

watch(
  () => messages.value.length,
  async () => {
    await nextTick();

    // If older messages were just prepended, restore scroll position
    if (wasLoadingOlder && streamRef.value) {
      const element = streamRef.value;
      const newScrollHeight = element.scrollHeight;
      const heightDelta = newScrollHeight - preUpdateScrollHeight;

      if (heightDelta > 0) {
        isRestoringScroll = true;
        element.scrollTop = preUpdateScrollTop + heightDelta;
        // Allow scroll handler to settle before re-enabling load-older detection
        requestAnimationFrame(() => {
          isRestoringScroll = false;
        });
      }

      wasLoadingOlder = false;
      return;
    }

    scheduleScrollToBottom();
  },
);

// Capture scroll position before older messages start loading
watch(
  isLoadingOlder,
  (loading) => {
    if (loading && streamRef.value) {
      preUpdateScrollHeight = streamRef.value.scrollHeight;
      preUpdateScrollTop = streamRef.value.scrollTop;
      wasLoadingOlder = true;
    }
  },
);

watch(
  () => props.sessionId,
  async () => {
    keepPinnedToBottom = true;
    showJumpToLatest.value = false;
    await nextTick();
    scrollToBottom();
  },
);

/** Points a sub-agent call's row at the session it started, once that session exists. */
function withDelegation(item: ToolCardItem, part: AccumulatedToolPart): ToolCardItem {
  if (!isSubagentTool(part.tool)) {
    return item;
  }

  const delegation = delegations.value.find((candidate) =>
    (candidate.parentToolCallId === part.callId || candidate.parentToolCallId === part.partId)
    && candidate.childSessionId);
  if (!delegation?.childSessionId) {
    return item;
  }

  const childSession = sessions.value.find((session) => session.session.id === delegation.childSessionId);
  const childInstanceId = childSession?.instanceId ?? delegation.childSessionId;
  return {
    ...item,
    delegation: {
      href: `/sessions/${delegation.childSessionId}?instanceId=${childInstanceId}&parentSessionId=${props.sessionId}`,
      childSessionId: delegation.childSessionId,
      childInstanceId,
      parentSessionId: props.sessionId,
      agent: subagentKind(part),
      task: subagentTask(part) || delegation.title,
      status: delegation.status,
    },
  };
}

function getDisplayAuthor(message: AccumulatedMessage): string {
  if (message.role === "user") {
    return "You";
  }

  return formatAgentDisplayName(message.agent ?? "Assistant");
}

function getSenderKey(role: AccumulatedMessage["role"], author?: string | null): string {
  if (role === "user") {
    return "user";
  }

  return normalizeIdentity(author ?? "Assistant");
}

function getClusterPosition(
  groupedWithPrevious: boolean,
  groupedWithNext: boolean,
): ActivityMessage["clusterPosition"] {
  if (groupedWithPrevious && groupedWithNext) {
    return "middle";
  }

  if (groupedWithPrevious) {
    return "last";
  }

  if (groupedWithNext) {
    return "first";
  }

  return "single";
}

function isSameSender(previous: ActivityMessage | undefined, next: ActivityMessage | undefined): boolean {
  if (!previous || !next) {
    return false;
  }

  return previous.role === next.role && previous.senderKey === next.senderKey;
}

function formatAgentDisplayName(author: string): string {
  const normalizedAuthor = author.replace(/[_-]+/g, " ").replace(/\s+/g, " ").trim();

  if (!normalizedAuthor) {
    return "Assistant";
  }

  if (normalizedAuthor.toLowerCase() !== normalizedAuthor) {
    return normalizedAuthor;
  }

  return normalizedAuthor.replace(/(^|[\s(])([a-z])/g, (_match, prefix: string, character: string) => {
    return `${prefix}${character.toUpperCase()}`;
  });
}

function normalizeIdentity(value: string): string {
  return value.replace(/[_-]+/g, " ").replace(/\s+/g, " ").trim().toLowerCase();
}

function hasVisibleMessageContent(message: ActivityMessage): boolean {
  return message.body.trim().length > 0
    || message.images.length > 0
    || (message.tools?.length ?? 0) > 0
    || (message.questionParts?.length ?? 0) > 0
    // A turn can fail before it produces anything; the failure is the content.
    || message.turnError != null;
}

function hasRenderableAssistantContent(message: AccumulatedMessage): boolean {
  if (message.role !== "assistant") {
    return false;
  }

  return message.parts.some((part) => {
    if (part.type === "tool") {
      return true;
    }

    if (part.type === "file") {
      return Boolean(part.filename?.trim() || part.url);
    }

    if (part.type === "text") {
      return part.text.trim().length > 0;
    }

    if (part.type === "reasoning") {
      return ((part.summary ?? part.text) || "").trim().length > 0;
    }

    return false;
  });
}

function renderMessageBody(parts: readonly AccumulatedPart[]): string {
  const bodyParts = parts
    .map((part) => renderMessagePart(part))
    .filter((part): part is string => part !== null);

  return bodyParts.join("\n\n");
}

function renderMessagePart(part: AccumulatedPart): string | null {
  if (part.type === "text") {
    return part.text;
  }

  if (part.type === "reasoning") {
    // Reasoning is now rendered separately, not in the markdown body
    return null;
  }

  if (part.type === "file") {
    if (part.mime.startsWith("image/")) {
      return null; // Images are rendered as thumbnails, not inline text
    }
    const label = part.filename?.trim() || "Attached file";
    return part.url ? `[${label}](${part.url})` : label;
  }

  return null;
}

function handleExpandVisual(payload: VisualPayload): void {
  canvasesStore.openVisual(props.sessionId, payload);
  showRightPanel();
}

function handleShowCanvas(canvasId: string): void {
  void focusServerCanvas(props.sessionId, canvasId);
  showRightPanel();
}
</script>

<template>
  <div class="activity-stream-shell">
    <button
      v-if="showJumpToLatest"
      type="button"
      class="jump-to-latest"
      @click="handleJumpToLatest"
    >
      Jump to latest
    </button>
    <section
      ref="streamRef"
      class="activity-stream"
      aria-label="Activity stream"
      data-testid="activity-stream"
      @scroll.passive="updatePinnedState"
    >
      <!-- Partial snapshot indicator -->
      <div
        v-if="isPartial"
        class="partial-snapshot-banner"
        role="status"
        aria-live="polite"
      >
        <span class="partial-snapshot-icon">⚠️</span>
        <span>Connection to live session is temporarily unavailable. Showing cached data.</span>
      </div>

      <!-- Load older messages indicator -->
      <div
        v-if="isLoadingOlder"
        class="load-older-indicator"
      >
        <span class="load-older-spinner" />
        Loading older messages…
      </div>
      <button
        v-else-if="hasMore"
        type="button"
        class="load-older-button"
        @click="loadOlder"
      >
        Load older messages
      </button>

      <div
        v-for="message in messages"
        :key="message.id"
        class="activity-message"
        :data-message-id="message.id"
        :class="[
          `activity-message--${message.role}`,
          `activity-message--${message.clusterPosition}`,
          { 'activity-message--shown': highlightedMessageId === message.id },
        ]"
      >
        <ReasoningBlock
          v-for="reasoning in message.reasoningParts ?? []"
          :key="reasoning.partId"
          :text="reasoning.text"
          :summary="reasoning.summary"
          :created-at="message.createdAt"
        />
        <div
          v-if="message.background"
          class="background-note"
          :class="`background-note--${message.background.state}`"
          data-testid="background-note"
        >
          <component
            :is="message.background.kind === 'subagent' ? Bot : TerminalSquare"
            class="background-note__icon"
            aria-hidden="true"
          />
          <span class="background-note__label">{{ message.background.kind === "subagent" ? "Background helper" : "Background command" }}</span>
          <span class="background-note__task">{{ message.background.label }}</span>
          <span class="background-note__state">{{ message.background.state }}</span>
        </div>
        <a
          v-if="message.peer"
          class="peer-from"
          :class="{ 'peer-from--failed': message.peerOutcome === 'failed' }"
          :href="`/sessions/${encodeURIComponent(message.peer.sessionId)}`"
          data-testid="peer-from"
          @click="handlePeerLinkClick($event, message.peer)"
        >
          <Bot
            class="peer-from__icon"
            aria-hidden="true"
          />
          <span
            v-if="!message.peerOutcome"
            class="peer-from__label"
          >From</span>
          <span class="peer-from__title">{{ message.peer.title }}</span>
          <span
            v-if="message.peerOutcome"
            class="peer-from__label peer-from__outcome"
          >{{ message.peerOutcome }}</span>
          <ArrowUpRight
            class="peer-from__icon"
            aria-hidden="true"
          />
        </a>
        <MessageBubble
          :author="message.author"
          :model-name="message.modelName"
          :role="message.role"
          :created-at="message.createdAt"
          :body="message.body"
          :images="message.images"
          :tools="message.tools"
          :question-parts="message.questionParts"
          :session-id="props.sessionId"
          :show-identity="message.showIdentity"
          :cluster-position="message.clusterPosition"
          @expand-visual="handleExpandVisual"
          @show-canvas="handleShowCanvas"
        />
        <div
          v-if="message.turnError"
          class="turn-failure"
          data-testid="turn-failure"
        >
          <div class="turn-failure__head">
            <TriangleAlert
              class="turn-failure__icon"
              aria-hidden="true"
            />
            <span class="turn-failure__title">This turn stopped early</span>
          </div>
          <p class="turn-failure__message">{{ message.turnError.message }}</p>
          <div class="turn-failure__foot">
            <span class="turn-failure__name">{{ message.turnError.name }}</span>
            <button
              v-if="lastUserPrompt"
              class="turn-failure__retry"
              type="button"
              data-testid="turn-failure-retry"
              :disabled="!canSend"
              @click="handleRetryTurn"
            >
              <RotateCw
                class="turn-failure__retry-icon"
                aria-hidden="true"
              />
              Retry
            </button>
          </div>
        </div>
        <div
          v-if="message.optimisticStatus === 'needs_retry'"
          class="optimistic-retry"
          :class="`optimistic-retry--${message.role}`"
        >
          Not confirmed yet. Retry from the composer if this did not send.
        </div>
      </div>

      <!-- Streaming indicator -->
      <div
        v-if="isStreaming"
        class="streaming-indicator"
      >
        <WorkingIndicator :since="turnStartedAt" />
      </div>
    </section>
  </div>
</template>

<style scoped>
/* A round brought forward from the Turns canvas is marked just long enough to find it. */
.activity-message--shown {
  border-radius: var(--radius-card);
  outline: 2px solid color-mix(in srgb, var(--accent) 60%, transparent);
  outline-offset: 4px;
  transition: outline-color 400ms ease-out;
}

@media (prefers-reduced-motion: reduce) {
  .activity-message--shown {
    transition: none;
  }
}

.activity-stream-shell {
  position: relative;
  display: flex;
  flex-direction: column;
  flex: 1;
  min-height: 0;
  overflow: hidden;
}

.activity-stream {
  --activity-bubble-width: 100%;
  flex: 1;
  display: flex;
  flex-direction: column;
  min-height: 0;
  overflow-y: auto;
  padding: 24px 32px 12px;
  scrollbar-width: thin;
  scrollbar-color: var(--muted) transparent;
}

.jump-to-latest {
  position: absolute;
  right: 24px;
  bottom: 20px;
  z-index: 2;
  display: inline-flex;
  padding: 8px 12px;
  border: 1px solid var(--border);
  border-radius: 999px;
  background: var(--card-bg);
  color: var(--text);
  font-size: 13px;
  line-height: 1;
  box-shadow: 0 8px 24px -8px rgba(0, 0, 0, 0.35);
  cursor: pointer;
  transition: border-color var(--transition);
}

.jump-to-latest:hover {
  border-color: color-mix(in srgb, var(--accent) 55%, var(--border));
}

.load-older-indicator,
.load-older-button {
  display: flex;
  align-items: center;
  justify-content: center;
  gap: 8px;
  padding: 8px 0;
  color: var(--muted);
  font-size: 13px;
}

.load-older-button {
  border: none;
  background: none;
  cursor: pointer;
  opacity: 0.7;
  transition: opacity var(--transition);
}

.load-older-button:hover {
  opacity: 1;
  color: var(--text);
}

.load-older-spinner {
  width: 14px;
  height: 14px;
  border: 2px solid color-mix(in srgb, var(--accent) 30%, transparent);
  border-top-color: var(--accent);
  border-radius: 50%;
  animation: spin 0.6s linear infinite;
}

.partial-snapshot-banner {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 12px 16px;
  margin-bottom: 16px;
  background: color-mix(in srgb, var(--idle) 10%, transparent);
  border: 1px solid color-mix(in srgb, var(--idle) 30%, transparent);
  border-radius: var(--radius-card);
  color: var(--idle);
  font-size: 13px;
  line-height: 1.4;
}

.partial-snapshot-icon {
  font-size: 1rem;
  flex-shrink: 0;
}

.turn-failure {
  width: var(--activity-bubble-width);
  margin: 4px 0 10px;
  padding: 10px 12px;
  border: 1px solid color-mix(in srgb, var(--error) 35%, var(--border));
  border-radius: var(--radius-card);
  background: color-mix(in srgb, var(--error) 7%, var(--card-bg));
  align-self: flex-start;
}

.turn-failure__head {
  display: flex;
  align-items: center;
  gap: 6px;
}

.turn-failure__icon {
  width: 14px;
  height: 14px;
  flex: none;
  color: var(--error);
}

.turn-failure__title {
  color: var(--text);
  font-size: 13px;
  font-weight: 600;
}

.turn-failure__message {
  margin: 6px 0 0;
  color: var(--text);
  font-size: 13px;
  line-height: 1.45;
  overflow-wrap: anywhere;
}

.turn-failure__foot {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
  margin-top: 8px;
}

.turn-failure__name {
  color: var(--muted);
  font-size: 11px;
  font-family: var(--font-mono, ui-monospace, monospace);
}

.turn-failure__retry {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  padding: 4px 10px;
  border: 1px solid var(--border);
  border-radius: 999px;
  background: var(--panel-bg);
  color: var(--text);
  font-size: 12px;
  cursor: pointer;
  transition: var(--transition);
}

.turn-failure__retry:hover:not(:disabled) {
  border-color: var(--accent);
  color: var(--accent);
}

.turn-failure__retry:disabled {
  opacity: 0.5;
  cursor: not-allowed;
}

.turn-failure__retry-icon {
  width: 12px;
  height: 12px;
}

.optimistic-retry {
  width: var(--activity-bubble-width);
  margin: 4px 0 8px;
  color: var(--idle);
  font-size: 12px;
}

.optimistic-retry--user {
  align-self: flex-end;
  text-align: right;
}

.optimistic-retry--assistant {
  align-self: flex-start;
}

@keyframes spin {
  to {
    transform: rotate(360deg);
  }
}

/* One centred reading column; space between turns instead of divider lines. */
.activity-message {
  display: flex;
  flex-direction: column;
  width: 100%;
  max-width: 760px;
  gap: 0;
  margin: 0 auto 20px;
}

.activity-message--assistant {
  align-items: flex-start;
}

.activity-message--user {
  align-items: flex-end;
}

.activity-message--first,
.activity-message--middle {
  margin-bottom: 6px;
}

.streaming-indicator {
  width: 100%;
  max-width: 760px;
  margin: 0 auto 12px;
  padding: 4px 0;
  box-sizing: border-box;
}

.background-note {
  display: flex;
  align-items: center;
  gap: 6px;
  align-self: flex-start;
  max-width: 100%;
  margin-bottom: 4px;
  padding: 2px 8px;
  border: 1px solid var(--border);
  border-radius: var(--radius-btn);
  background: var(--surface-2);
  color: var(--muted);
  font-size: 12px;
}

.background-note__icon {
  width: 13px;
  height: 13px;
  flex-shrink: 0;
}

.background-note__label {
  font-weight: 500;
  color: var(--text);
}

.background-note__task {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  font-family: var(--font-mono-stack);
}

.background-note__state {
  flex-shrink: 0;
  color: var(--complete);
}

.background-note--error .background-note__state,
.background-note--cancelled .background-note__state {
  color: var(--error);
}

.peer-from {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  max-width: min(100%, 380px);
  margin: 0 0 6px;
  padding: 4px 10px;
  border: 1px solid color-mix(in srgb, var(--border) 90%, transparent);
  border-radius: 999px;
  background: color-mix(in srgb, var(--card-bg, var(--panel-bg)) 96%, var(--accent-dim) 4%);
  color: var(--muted);
  font-size: 0.75rem;
  text-decoration: none;
  transition: border-color var(--transition) ease, background-color var(--transition) ease;
}

.peer-from:hover {
  border-color: color-mix(in srgb, var(--primary, #6366f1) 24%, var(--border));
  background: color-mix(in srgb, var(--card-bg, var(--panel-bg)) 88%, var(--accent-dim) 12%);
}

.peer-from--failed .peer-from__outcome {
  color: var(--error);
}

.peer-from__icon {
  flex-shrink: 0;
  width: 0.85rem;
  height: 0.85rem;
}

.peer-from__title {
  min-width: 0;
  overflow: hidden;
  color: var(--text);
  font-weight: 600;
  text-overflow: ellipsis;
  white-space: nowrap;
}
</style>
