<script setup lang="ts">
import { computed, nextTick, onMounted, onUnmounted, ref, watch } from "vue";
import { ArrowUpRight, Bot } from "lucide-vue-next";
import { useRouter } from "@tanstack/vue-router";
import { storeToRefs } from "pinia";
import MessageBubble from "@/components/session/MessageBubble.vue";
import { useSessionEvents } from "@/composables/use-session-events";
import { clearSentPrompts, reconcileSentPrompts, useSentPrompts } from "@/composables/use-send-prompt";
import type { CommandEventName } from "@/lib/command-events";
import { formatTimestamp } from "@/lib/format-utils";
import type { AccumulatedMessage, AccumulatedPart, AccumulatedToolPart } from "@/lib/api-types";
import { getToolLabel } from "@/lib/tool-labels";
import { useSessionsStore } from "@/stores/sessions";
import { dispatchSessionUpsert } from "@/lib/session-sync";

interface DiffLine {
  type: "add" | "remove" | "context";
  content: string;
  oldLineNumber?: number;
  newLineNumber?: number;
}

interface ToolCardItem {
  id: string;
  title: string;
  kind?: string;
  status?: string;
  summary?: string;
  output?: string;
  diffLines?: DiffLine[];
  initiallyCollapsed?: boolean;
}

interface ActivityMessage {
  id: string;
  author: string;
  senderKey: string;
  role: AccumulatedMessage["role"];
  timestamp: string;
  body: string;
  tools?: ToolCardItem[];
  delegationLinks: DelegationLink[];
  clusterPosition: "single" | "first" | "middle" | "last";
  showIdentity: boolean;
}

interface DelegationLink {
  id: string;
  href: string;
  title: string;
  status: string;
  statusKey: string;
}

const props = defineProps<{
  sessionId: string;
  instanceId?: string;
}>();

const router = useRouter();
const sessionsStore = useSessionsStore();
const { sessions } = storeToRefs(sessionsStore);

const selectedSession = computed(() => {
  return sessions.value.find((session) => session.session.id === props.sessionId) ?? null;
});

const resolvedInstanceId = computed(() => props.instanceId ?? selectedSession.value?.instanceId ?? "");

const { messages: sessionMessages, delegations, forceIdle } = useSessionEvents(
  computed(() => props.sessionId),
  resolvedInstanceId,
);
const { sentPrompts } = useSentPrompts(props.sessionId);
const streamRef = ref<HTMLElement | null>(null);
const showJumpToLatest = ref(false);

const SCROLL_BOTTOM_THRESHOLD = 80;

let mutationObserver: MutationObserver | null = null;
let keepPinnedToBottom = true;
let scrollFrame: number | null = null;
const cleanupCallbacks: Array<() => void> = [];

watch(
  sessionMessages,
  (nextMessages) => {
    if (selectedSession.value) {
      dispatchSessionUpsert({ ...selectedSession.value });
    }

    reconcileSentPrompts(props.sessionId, nextMessages);

    if (sentPrompts.value.length === 0) {
      return;
    }

    if (nextMessages.some((message) => hasRenderableAssistantContent(message))) {
      clearSentPrompts(props.sessionId);
      forceIdle();
    }
  },
  { immediate: true, deep: true },
);

const deliveredMessages = computed<ActivityMessage[]>(() => {
  return sessionMessages.value
    .map((message) => {
      const author = getDisplayAuthor(message);

      return {
        id: message.messageId,
        author,
        senderKey: getSenderKey(message.role, message.agent),
        role: message.role,
        timestamp: formatTimestamp(message.createdAt),
        body: renderMessageBody(message.parts),
        tools: message.parts
          .filter((part): part is AccumulatedToolPart => part.type === "tool")
          .map(toToolCardItem),
        delegationLinks: getDelegationLinks(message),
        clusterPosition: "single" as const,
        showIdentity: true,
      } satisfies ActivityMessage;
    })
    .filter((message) => message.role === "user" || hasVisibleMessageContent(message));
});

const optimisticMessages = computed<ActivityMessage[]>(() => {
  return sentPrompts.value.map((prompt): ActivityMessage => ({
    id: `optimistic-${prompt.id}`,
    author: "You",
    senderKey: "user",
    role: "user",
    timestamp: prompt.timestamp,
    body: prompt.body,
    tools: [],
    delegationLinks: [],
    clusterPosition: "single",
    showIdentity: true,
  }));
});

const messages = computed<ActivityMessage[]>(() => {
  const baseMessages = [...deliveredMessages.value, ...optimisticMessages.value];

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

function handleDelegationLinkClick(event: MouseEvent, delegationLink: DelegationLink): void {
  event.preventDefault();
  const url = new URL(delegationLink.href, window.location.origin);
  const sessionId = decodeURIComponent(url.pathname.replace("/sessions/", ""));
  const instanceId = url.searchParams.get("instanceId") ?? undefined;
  const parentSessionId = url.searchParams.get("parentSessionId") ?? undefined;

  void router.navigate({
    to: "/sessions/$id",
    params: { id: sessionId },
    search: { instanceId, parentSessionId },
  });
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
  cleanupCallbacks.push(registerWindowCommandListener("weave:command-focus-prompt", handleFocusPromptCommand));
  cleanupCallbacks.push(registerWindowCommandListener("weave:command-copy-session-id", handleCopySessionIdCommand));
  cleanupCallbacks.push(registerWindowCommandListener("weave:command-export-conversation", handleExportConversationCommand));
});

onUnmounted(() => {
  mutationObserver?.disconnect();
  mutationObserver = null;

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
    scheduleScrollToBottom();
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

function getDelegationLinks(message: AccumulatedMessage): DelegationLink[] {
  const taskToolParts = message.parts.filter(
    (part): part is AccumulatedToolPart => part.type === "tool" && part.tool === "task",
  );

  return taskToolParts.flatMap((part) => {
    return delegations.value.flatMap((delegation) => {
      if ((delegation.parentToolCallId !== part.callId && delegation.parentToolCallId !== part.partId) || !delegation.childSessionId) {
        return [];
      }

      const childSession = sessions.value.find(
        (session) => session.session.id === delegation.childSessionId,
      );

      const childInstanceId = childSession?.instanceId ?? delegation.childSessionId;

      if (!childInstanceId) {
        return [];
      }

      return [{
        id: delegation.delegationId,
        href: `/sessions/${delegation.childSessionId}?instanceId=${childInstanceId}&parentSessionId=${props.sessionId}`,
        title: delegation.title,
        status: formatToolStatus(delegation.status),
        statusKey: delegation.status,
      } satisfies DelegationLink];
    });
  });
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
    || (message.tools?.length ?? 0) > 0
    || message.delegationLinks.length > 0;
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
    const reasoningText = (part.summary ?? part.text).trim();
    return reasoningText ? reasoningText.split("\n").map((line) => `> ${line}`).join("\n") : null;
  }

  if (part.type === "file") {
    const label = part.filename?.trim() || "Attached file";
    return part.url ? `[${label}](${part.url})` : label;
  }

  return null;
}

function toToolCardItem(part: AccumulatedToolPart): ToolCardItem {
  const state = asRecord(part.state);
  const input = asRecord(state?.input);
  const title = getToolLabel(part.tool, input) || part.tool;

  return {
    id: part.partId,
    title,
    kind: part.tool,
    status: formatToolStatus(state?.status),
    summary: getStringValue(state?.summary),
    output: stringifyToolValue(state?.output),
    diffLines: getDiffLines(state),
    initiallyCollapsed: state?.status === "completed",
  };
}

function getDiffLines(state: Record<string, unknown> | null): DiffLine[] {
  const candidates = [state?.diffLines, state?.diff, state?.patch];

  for (const candidate of candidates) {
    const lines = normalizeDiffLines(candidate);

    if (lines.length > 0) {
      return lines;
    }
  }

  return [];
}

function normalizeDiffLines(value: unknown): DiffLine[] {
  if (!Array.isArray(value)) {
    return [];
  }

  return value.flatMap((entry) => {
    const record = asRecord(entry);
    const content = getStringValue(record?.content) ?? getStringValue(record?.text) ?? getStringValue(record?.line);
    const type = getStringValue(record?.type) ?? inferDiffType(content);

    if (!content || !type || !isDiffType(type)) {
      return [];
    }

    return [{
      type,
      content,
      oldLineNumber: getNumberValue(record?.oldLineNumber),
      newLineNumber: getNumberValue(record?.newLineNumber),
    } satisfies DiffLine];
  });
}

function inferDiffType(content: string | undefined): DiffLine["type"] | null {
  if (!content) {
    return null;
  }

  if (content.startsWith("+")) {
    return "add";
  }

  if (content.startsWith("-")) {
    return "remove";
  }

  return "context";
}

function isDiffType(value: string): value is DiffLine["type"] {
  return value === "add" || value === "remove" || value === "context";
}

function formatToolStatus(value: unknown): string {
  const status = getStringValue(value);
  if (!status) {
    return "Pending";
  }

  return status.charAt(0).toUpperCase() + status.slice(1);
}

function stringifyToolValue(value: unknown): string | undefined {
  if (typeof value === "string") {
    return value;
  }

  if (value == null) {
    return undefined;
  }

  try {
    return JSON.stringify(value, null, 2);
  } catch {
    return String(value);
  }
}

function getStringValue(value: unknown): string | undefined {
  return typeof value === "string" && value.trim() ? value : undefined;
}

function getNumberValue(value: unknown): number | undefined {
  return typeof value === "number" && Number.isFinite(value) ? value : undefined;
}

function asRecord(value: unknown): Record<string, unknown> | null {
  if (!value || typeof value !== "object" || Array.isArray(value)) {
    return null;
  }

  return value as Record<string, unknown>;
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
      <div
        v-for="message in messages"
        :key="message.id"
        class="activity-message"
        :class="[
          `activity-message--${message.role}`,
          `activity-message--${message.clusterPosition}`,
        ]"
      >
        <MessageBubble
          :author="message.author"
          :role="message.role"
          :timestamp="message.timestamp"
          :body="message.body"
          :tools="message.tools"
          :show-identity="message.showIdentity"
          :cluster-position="message.clusterPosition"
        />
        <div
          v-if="message.delegationLinks.length > 0"
          class="delegation-links"
          :class="`delegation-links--${message.role}`"
        >
          <a
            v-for="delegationLink in message.delegationLinks"
            :key="delegationLink.id"
            class="delegation-link"
            :class="`delegation-link--${delegationLink.statusKey}`"
            :href="delegationLink.href"
            @click="handleDelegationLinkClick($event, delegationLink)"
          >
            <div class="delegation-link__header">
              <span class="delegation-link__eyebrow">
                <Bot
                  class="delegation-link__eyebrow-icon"
                  aria-hidden="true"
                />
                Subagent task
              </span>
              <span class="delegation-link-status">
                {{ delegationLink.status }}
              </span>
            </div>
            <div class="delegation-link__body">
              <span class="delegation-link__title">{{ delegationLink.title }}</span>
              <span class="delegation-link__cta">
                Open session
                <ArrowUpRight
                  class="delegation-link__cta-icon"
                  aria-hidden="true"
                />
              </span>
            </div>
          </a>
        </div>
      </div>
    </section>
  </div>
</template>

<style scoped>
.activity-stream-shell {
  position: relative;
  display: flex;
  flex-direction: column;
  flex: 1;
  min-height: 0;
  overflow: hidden;
}

.activity-stream {
  --activity-bubble-width: min(95%, 1040px);
  flex: 1;
  display: flex;
  flex-direction: column;
  min-height: 0;
  overflow-y: auto;
  padding: 20px 24px;
}

.jump-to-latest {
  position: absolute;
  right: 24px;
  bottom: 20px;
  z-index: 2;
  display: inline-flex;
  padding: 8px 12px;
  border: 1px solid rgba(129, 140, 248, 0.35);
  border-radius: 999px;
  background: rgba(24, 24, 27, 0.92);
  color: #e4e4e7;
  font-size: 0.875rem;
  line-height: 1;
  box-shadow: 0 8px 24px rgba(0, 0, 0, 0.24);
  cursor: pointer;
}

.jump-to-latest:hover {
  border-color: rgba(129, 140, 248, 0.65);
  background: rgba(39, 39, 42, 0.96);
}

.activity-message {
  display: flex;
  flex-direction: column;
  width: 100%;
  gap: 8px;
  margin-bottom: 16px;
}

.activity-message--assistant {
  align-items: flex-start;
}

.activity-message--user {
  align-items: flex-end;
}

.activity-message--first,
.activity-message--middle {
  margin-bottom: 4px;
}

.delegation-links {
  display: flex;
  flex-direction: column;
  width: var(--activity-bubble-width);
  box-sizing: border-box;
  gap: 6px;
  margin-top: 2px;
  padding: 0 12px;
}

.delegation-links--user {
  align-items: flex-end;
  text-align: right;
}

.delegation-link {
  display: flex;
  flex-direction: column;
  gap: 8px;
  min-width: min(100%, 320px);
  max-width: min(100%, 420px);
  padding: 12px 14px;
  border: 1px solid color-mix(in srgb, var(--border) 82%, transparent);
  border-radius: 14px;
  background: color-mix(in srgb, var(--card, var(--panel-bg)) 88%, white 12%);
  color: inherit;
  text-decoration: none;
  box-shadow: 0 8px 24px rgba(15, 23, 42, 0.08);
  transition: border-color 140ms ease, background-color 140ms ease, transform 140ms ease;
}

.delegation-link:hover {
  transform: translateY(-1px);
  border-color: color-mix(in srgb, var(--primary, #6366f1) 45%, var(--border));
  background: color-mix(in srgb, var(--card, var(--panel-bg)) 78%, white 22%);
}

.delegation-link:focus-visible {
  outline: 2px solid color-mix(in srgb, var(--primary, #6366f1) 65%, white 35%);
  outline-offset: 2px;
}

.delegation-link__header,
.delegation-link__body {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
}

.delegation-link__eyebrow {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  color: var(--muted-foreground, var(--color-text-muted, #6b7280));
  font-size: 0.7rem;
  font-weight: 700;
  letter-spacing: 0.08em;
  text-transform: uppercase;
}

.delegation-link__eyebrow-icon,
.delegation-link__cta-icon {
  width: 0.9rem;
  height: 0.9rem;
}

.delegation-link__title {
  font-size: 0.95rem;
  font-weight: 600;
  line-height: 1.35;
  color: var(--foreground, var(--text, #f4f4f5));
}

.delegation-link__cta {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  flex-shrink: 0;
  color: color-mix(in srgb, var(--primary, #6366f1) 78%, white 22%);
  font-size: 0.78rem;
  font-weight: 600;
}

.delegation-link-status {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  flex-shrink: 0;
  padding: 4px 8px;
  border-radius: 999px;
  background: color-mix(in srgb, var(--muted) 78%, transparent);
  color: var(--muted-foreground, var(--color-text-muted, #6b7280));
  font-size: 0.75rem;
  font-weight: 600;
}

.delegation-link--running .delegation-link-status {
  background: color-mix(in srgb, var(--primary, #6366f1) 18%, transparent);
  color: color-mix(in srgb, var(--primary, #6366f1) 70%, white 30%);
}

.delegation-link--completed .delegation-link-status {
  background: color-mix(in srgb, #22c55e 18%, transparent);
  color: #16a34a;
}

.delegation-link--error .delegation-link-status,
.delegation-link--cancelled .delegation-link-status {
  background: color-mix(in srgb, #ef4444 18%, transparent);
  color: #dc2626;
}
</style>
