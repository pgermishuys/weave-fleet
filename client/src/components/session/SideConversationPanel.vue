<script setup lang="ts">
import { computed, nextTick, onMounted, onUnmounted, shallowRef, useTemplateRef, watch } from "vue";
import { useRouter } from "@tanstack/vue-router";
import { Minus, MessageCircleQuestionMark, SquareArrowOutUpRight, Undo2, X } from "lucide-vue-next";
import ActivityStream from "@/components/session/ActivityStream.vue";
import { Button } from "@/components/ui/button";
import { SIDE_DISCARD_UNDO_MS, useSideConversation } from "@/composables/use-side-conversation";

/**
 * A session's side conversation (`/btw`), docked above the composer. It's a fork of the session made at its last
 * finished turn, so only what was asked in it shows here; the session itself carries on undisturbed. While it's open
 * the composer talks to it. Minimize folds it into a tab on the composer's top edge (the composer talks to the session
 * again; hover the tab to peek at the answer, click it or press Ctrl+/ to open it). × discards it, with Undo for a few
 * seconds; Keep as session makes it a session of its own.
 */
const props = defineProps<{
  sessionId: string;
}>();

const router = useRouter();
const {
  side,
  minimized,
  unread,
  working,
  latestAnswer,
  discarded,
  starting,
  setMinimized,
  toggleMinimized,
  reportProgress,
  close,
  undoDiscard,
  keep,
} = useSideConversation(() => props.sessionId);

const title = computed(() => side.value?.title.replace(/^btw:\s*/, "") ?? starting.value ?? "");
const shown = computed(() => side.value !== null || starting.value !== null);
const isMac = typeof navigator !== "undefined" && /Mac|iPhone|iPad/.test(navigator.platform);
const shortcut = isMac ? "⌘ /" : "Ctrl /";

const bodyRef = useTemplateRef<HTMLElement>("body");

// Where the conversation was scrolled when it was folded, to put it back when it opens.
let savedScroll: { top: number; atBottom: boolean } | null = null;

function streamElement(): HTMLElement | null {
  return bodyRef.value?.querySelector<HTMLElement>(".activity-stream") ?? null;
}

watch(minimized, async (isMinimized, wasMinimized) => {
  if (isMinimized === wasMinimized) return;
  if (isMinimized) {
    const stream = streamElement();
    savedScroll = stream
      ? { top: stream.scrollTop, atBottom: stream.scrollHeight - stream.scrollTop - stream.clientHeight <= 80 }
      : null;
    peeking.value = false;
    return;
  }

  // Opened: the conversation as it was left (or its end, where a new answer is), and the composer is its input again.
  await nextTick();
  requestAnimationFrame(() => {
    const stream = streamElement();
    if (stream && savedScroll) {
      stream.scrollTop = savedScroll.atBottom ? stream.scrollHeight : savedScroll.top;
      stream.dispatchEvent(new Event("scroll"));
    }
    document.querySelector<HTMLTextAreaElement>('[data-testid="prompt-input"]')?.focus();
  });
});

async function handleKeep(): Promise<void> {
  const kept = await keep();
  if (kept) {
    await router.navigate({
      to: "/sessions/$id",
      params: { id: kept.sessionId },
      search: { instanceId: kept.instanceId, parentSessionId: undefined },
    });
  }
}

// Peek: hover or focus shows it (CSS); a long press does on touch, where a tap opens.
const peeking = shallowRef(false);
let pressTimer: ReturnType<typeof setTimeout> | null = null;
let pressPeeked = false;

function handleTabPointerDown(event: PointerEvent): void {
  if (event.pointerType !== "touch") return;
  pressPeeked = false;
  pressTimer = setTimeout(() => {
    pressPeeked = true;
    peeking.value = true;
  }, 450);
}

function handleTabPointerEnd(): void {
  if (pressTimer) clearTimeout(pressTimer);
  pressTimer = null;
}

function handleTabClick(event: MouseEvent): void {
  if (pressPeeked) {
    // The long press peeked; the lifting finger isn't a tap.
    pressPeeked = false;
    event.preventDefault();
    return;
  }
  void setMinimized(false);
}

/** Ctrl+/ (⌘/ on a Mac) opens or folds it. Not in a code editor, where it comments a line. */
function handleKeydown(event: KeyboardEvent): void {
  if (event.defaultPrevented || event.key !== "/" || !(event.ctrlKey || event.metaKey) || event.altKey || event.shiftKey) return;
  if (!side.value) return;
  if (event.target instanceof Element && event.target.closest(".cm-editor")) return;
  event.preventDefault();
  void toggleMinimized();
}

onMounted(() => window.addEventListener("keydown", handleKeydown));
onUnmounted(() => {
  window.removeEventListener("keydown", handleKeydown);
  if (pressTimer) clearTimeout(pressTimer);
});

const drainStyle = { animationDuration: `${SIDE_DISCARD_UNDO_MS}ms` };
</script>

<template>
  <section
    v-if="shown"
    class="side-conversation"
    :class="{ 'side-conversation--minimized': minimized }"
    aria-label="Side conversation"
    data-testid="side-conversation"
    :data-minimized="minimized"
  >
    <div class="side-conversation__fold">
      <div
        class="side-conversation__fold-inner"
        :inert="minimized || undefined"
      >
        <div class="side-conversation__card">
          <header class="side-conversation__head">
            <MessageCircleQuestionMark
              class="side-conversation__icon"
              aria-hidden="true"
            />
            <span class="side-conversation__label">btw</span>
            <span
              class="side-conversation__title"
              :title="title"
            >{{ title }}</span>
            <span class="side-conversation__hint">A fork at the last finished turn · the session carries on</span>
            <Button
              v-if="side"
              variant="toolbar-icon"
              size="sm"
              class="side-conversation__action"
              data-testid="side-conversation-keep"
              title="Keep as a session of its own"
              @click="handleKeep"
            >
              <SquareArrowOutUpRight
                class="side-conversation__action-icon"
                aria-hidden="true"
              />
              <span>Keep as session</span>
            </Button>
            <Button
              v-if="side"
              variant="toolbar-icon"
              size="sm"
              class="side-conversation__action"
              data-testid="side-conversation-minimize"
              :title="`Minimize (${shortcut}); the composer goes back to the session`"
              @click="setMinimized(true)"
            >
              <Minus
                class="side-conversation__action-icon"
                aria-hidden="true"
              />
              <span>Minimize</span>
            </Button>
            <Button
              variant="toolbar-icon"
              size="toolbar"
              data-testid="side-conversation-close"
              aria-label="Discard the side conversation"
              title="Discard (you can undo it for a few seconds)"
              :disabled="!side"
              @click="close"
            >
              <X
                class="side-conversation__action-icon"
                aria-hidden="true"
              />
            </Button>
          </header>
          <div
            ref="body"
            class="side-conversation__body"
          >
            <ActivityStream
              v-if="side"
              :key="side.sessionId"
              :session-id="side.sessionId"
              :after="side.boundaryMessageId"
              @progress="reportProgress"
            />
            <p
              v-else
              class="side-conversation__starting"
              role="status"
            >
              Forking the session at its last finished turn…
            </p>
          </div>
        </div>
      </div>
    </div>

    <div
      v-if="minimized && side"
      class="side-tab-wrap"
      :class="{ 'side-tab-wrap--peeking': peeking }"
    >
      <div
        class="side-peek"
        role="tooltip"
        data-testid="side-conversation-peek"
      >
        <span class="side-peek__eyebrow">btw · latest answer</span>
        <p class="side-peek__text">
          {{ working ? "Thinking about your question…" : latestAnswer ?? "No answer yet." }}
        </p>
        <span class="side-peek__foot">Click the tab to open · <kbd>{{ shortcut }}</kbd></span>
      </div>
      <div
        class="side-tab"
        data-testid="side-conversation-tab"
      >
        <button
          type="button"
          class="side-tab__open"
          data-testid="side-conversation-open"
          :aria-label="`Open the side conversation: ${title}`"
          @click="handleTabClick"
          @pointerdown="handleTabPointerDown"
          @pointerup="handleTabPointerEnd"
          @pointercancel="handleTabPointerEnd"
          @pointerleave="handleTabPointerEnd"
          @blur="peeking = false"
        >
          <MessageCircleQuestionMark
            class="side-conversation__icon"
            aria-hidden="true"
          />
          <span class="side-conversation__label">btw</span>
          <span class="side-tab__question">{{ title }}</span>
          <span
            class="side-tab__state"
            data-testid="side-conversation-state"
          >
            <template v-if="working">
              <span
                class="side-tab__dots"
                aria-hidden="true"
              ><i /><i /><i /></span>Thinking
            </template>
            <template v-else-if="unread">
              <span
                class="side-tab__unread"
                aria-hidden="true"
              />New answer
            </template>
            <template v-else-if="latestAnswer">Answered</template>
          </span>
        </button>
        <Button
          variant="toolbar-icon"
          size="toolbar"
          class="side-tab__close"
          data-testid="side-conversation-tab-close"
          aria-label="Discard the side conversation"
          title="Discard (you can undo it for a few seconds)"
          @click="close"
        >
          <X
            class="side-conversation__action-icon"
            aria-hidden="true"
          />
        </Button>
      </div>
    </div>
  </section>

  <Transition name="side-toast">
    <div
      v-if="discarded"
      class="side-toast"
      role="status"
      data-testid="side-conversation-undo-toast"
    >
      <div class="side-toast__row">
        <span>Side question discarded</span>
        <button
          type="button"
          class="side-toast__action"
          data-testid="side-conversation-undo"
          @click="undoDiscard"
        >
          <Undo2 aria-hidden="true" />
          Undo
        </button>
      </div>
      <div
        class="side-toast__drain"
        :style="drainStyle"
      />
    </div>
  </Transition>
</template>

<style scoped>
/* On the composer's column, right above it: the composer is its input while it's open. */
.side-conversation {
  flex-shrink: 0;
  padding: 6px 24px 0;
}

.side-conversation--minimized {
  padding-top: 0;
}

/* Minimize folds the card away where it stands, and the tab takes its place on the composer's edge. */
.side-conversation__fold {
  display: grid;
  grid-template-rows: 1fr;
  transition: grid-template-rows 280ms ease-out;
}

.side-conversation--minimized .side-conversation__fold {
  grid-template-rows: 0fr;
}

.side-conversation__fold-inner {
  min-height: 0;
  overflow: hidden;
}

.side-conversation__card {
  display: flex;
  flex-direction: column;
  max-width: 760px;
  height: min(42vh, 400px);
  margin: 0 auto;
  overflow: hidden;
  border: 1px solid color-mix(in srgb, var(--accent) 45%, var(--border));
  border-radius: calc(var(--radius-panel) + 2px);
  background: var(--card-bg);
}

.side-conversation__head {
  display: flex;
  align-items: center;
  gap: 8px;
  min-width: 0;
  padding: 8px 8px 8px 14px;
  border-bottom: 1px solid var(--border);
  font-size: 12px;
}

.side-conversation__icon {
  width: 14px;
  height: 14px;
  flex-shrink: 0;
  color: var(--accent);
}

.side-conversation__label {
  flex-shrink: 0;
  color: var(--accent);
  font-weight: 600;
}

.side-conversation__title {
  min-width: 0;
  overflow: hidden;
  color: var(--text);
  font-weight: 500;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.side-conversation__hint {
  flex: 1;
  min-width: 0;
  overflow: hidden;
  color: var(--muted);
  text-overflow: ellipsis;
  white-space: nowrap;
}

.side-conversation__action {
  height: 26px;
  flex-shrink: 0;
  font-size: 12px;
}

.side-conversation__action-icon {
  width: 14px;
  height: 14px;
}

.side-conversation__body {
  display: flex;
  flex: 1;
  flex-direction: column;
  min-height: 0;
}

/* The conversation's own stream, set closer: the card is its frame. */
.side-conversation__body :deep(.activity-stream) {
  padding: 12px 16px 8px;
}

.side-conversation__starting {
  margin: auto;
  color: var(--muted);
  font-size: 13px;
}

/* The tab sits on the composer's top edge, over its border, so the two read as one. */
.side-tab-wrap {
  position: relative;
  z-index: 2;
  display: flex;
  max-width: 760px;
  margin: 0 auto -5px;
}

.side-tab {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  max-width: min(100%, 520px);
  margin-left: 14px;
  padding: 3px 4px 5px 10px;
  border: 1px solid color-mix(in srgb, var(--accent) 45%, var(--border));
  border-bottom: 0;
  border-radius: 10px 10px 0 0;
  background: var(--card-bg);
  font-size: 12px;
  transform-origin: bottom left;
  animation: side-tab-in 260ms ease-out;
}

@keyframes side-tab-in {
  from {
    opacity: 0;
    transform: translateY(8px) scaleY(0.6);
  }
}

.side-tab__open {
  display: inline-flex;
  align-items: center;
  gap: 7px;
  min-width: 0;
  padding: 0;
  border: none;
  background: transparent;
  color: var(--text);
  font: inherit;
  cursor: pointer;
}

.side-tab__question {
  min-width: 0;
  overflow: hidden;
  font-weight: 500;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.side-tab__state {
  display: inline-flex;
  flex-shrink: 0;
  align-items: center;
  gap: 5px;
  color: var(--muted);
  white-space: nowrap;
}

.side-tab__state:not(:empty)::before {
  content: "·";
  margin-right: 1px;
}

.side-tab__dots {
  display: inline-flex;
  gap: 2px;
}

.side-tab__dots i {
  width: 4px;
  height: 4px;
  border-radius: 50%;
  background: currentColor;
  animation: side-dots 1.2s infinite;
}

.side-tab__dots i:nth-child(2) {
  animation-delay: 0.2s;
}

.side-tab__dots i:nth-child(3) {
  animation-delay: 0.4s;
}

@keyframes side-dots {
  0%, 80%, 100% {
    opacity: 0.25;
  }

  40% {
    opacity: 1;
  }
}

.side-tab__unread {
  width: 7px;
  height: 7px;
  flex-shrink: 0;
  border-radius: 50%;
  background: var(--accent);
  animation: side-unread 1.6s ease-out 2;
}

@keyframes side-unread {
  0% {
    box-shadow: 0 0 0 0 color-mix(in srgb, var(--accent) 60%, transparent);
  }

  80%, 100% {
    box-shadow: 0 0 0 7px transparent;
  }
}

.side-tab__close {
  flex-shrink: 0;
  width: 22px;
  height: 22px;
}

.side-peek {
  position: absolute;
  bottom: calc(100% + 8px);
  left: 14px;
  z-index: 5;
  display: grid;
  gap: 6px;
  width: min(380px, calc(100% - 28px));
  padding: 10px 12px;
  border: 1px solid var(--border);
  border-radius: var(--radius-card);
  background: var(--card-bg);
  box-shadow: 0 16px 36px -18px rgba(0, 0, 0, 0.5);
  font-size: 13px;
  opacity: 0;
  pointer-events: none;
  transform: translateY(4px);
  transition: opacity 160ms ease-out, transform 160ms ease-out;
}

.side-tab-wrap:hover .side-peek,
.side-tab-wrap:focus-within .side-peek,
.side-tab-wrap--peeking .side-peek {
  opacity: 1;
  transform: none;
}

.side-peek__eyebrow {
  color: var(--muted);
  font-size: 11px;
  font-weight: 600;
  letter-spacing: 0.06em;
  text-transform: uppercase;
}

.side-peek__text {
  display: -webkit-box;
  margin: 0;
  overflow: hidden;
  color: var(--text);
  line-height: 1.5;
  -webkit-box-orient: vertical;
  -webkit-line-clamp: 6;
  white-space: pre-wrap;
}

.side-peek__foot {
  color: var(--muted);
  font-size: 11.5px;
}

.side-peek__foot kbd {
  padding: 0 4px;
  border: 1px solid var(--border);
  border-radius: 4px;
  font-family: var(--font-mono-stack);
  font-size: 11px;
}

/* Inverted like the archive toast, so it reads over any panel; the bar drains while Undo is still possible. */
.side-toast {
  position: fixed;
  left: 50%;
  bottom: max(16px, env(safe-area-inset-bottom));
  z-index: 60;
  width: min(360px, calc(100vw - 32px));
  overflow: hidden;
  border-radius: var(--radius-card);
  background: var(--text);
  box-shadow: 0 12px 32px -12px rgba(0, 0, 0, 0.45);
  color: var(--main-bg);
  font-size: 13px;
  transform: translateX(-50%);
}

.side-toast__row {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
  padding: 9px 8px 9px 14px;
}

.side-toast__action {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  padding: 4px 10px;
  border: none;
  border-radius: var(--radius-btn);
  background: color-mix(in srgb, var(--main-bg) 18%, transparent);
  color: inherit;
  font: inherit;
  font-weight: 600;
  cursor: pointer;
}

.side-toast__action svg {
  width: 14px;
  height: 14px;
}

.side-toast__drain {
  height: 2px;
  background: color-mix(in srgb, var(--main-bg) 45%, transparent);
  transform-origin: left;
  animation: side-toast-drain linear forwards;
}

@keyframes side-toast-drain {
  to {
    transform: scaleX(0);
  }
}

.side-toast-enter-active,
.side-toast-leave-active {
  transition: opacity 180ms ease-out, transform 180ms ease-out;
}

.side-toast-enter-from,
.side-toast-leave-to {
  opacity: 0;
  transform: translate(-50%, 8px);
}

@media (prefers-reduced-motion: reduce) {
  .side-conversation__fold,
  .side-peek,
  .side-toast-enter-active,
  .side-toast-leave-active {
    transition: none;
  }

  .side-tab,
  .side-tab__dots i,
  .side-tab__unread,
  .side-toast__drain {
    animation: none;
  }
}

@media (max-width: 640px) {
  .side-conversation {
    padding: 6px 12px 0;
  }

  .side-conversation--minimized {
    padding-top: 0;
  }

  .side-conversation__hint,
  .side-conversation__action span {
    display: none;
  }
}
</style>
