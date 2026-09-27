<script setup lang="ts">
import { onMounted, onUnmounted, ref, watch } from "vue";
import { storeToRefs } from "pinia";
import { CircleArrowUp } from "lucide-vue-next";
import { NOTICE_HOLD_MS, NOTICE_STARTUP_QUIET_MS, NOTICE_TYPING_QUIET_MS, useNoticesStore } from "@/stores/notices";

const WAIT_POLL_MS = 500;

const store = useNoticesStore();
const { open, pinned, nextWaiting } = storeToRefs(store);

const card = ref<HTMLElement | null>(null);
const mountedAt = Date.now();
let lastTypedAt = 0;
let waitTimer: ReturnType<typeof setInterval> | undefined;
let holdTimer: ReturnType<typeof setTimeout> | undefined;
let holdLeft = NOTICE_HOLD_MS;
let holdStartedAt = 0;
let held = false;
/** Whether the hold is paused (hovered or focused), so the countdown bar pauses with it. */
const holding = ref(false);

function isTextField(target: EventTarget | null): boolean {
  if (!(target instanceof HTMLElement)) return false;
  return target.isContentEditable || target.tagName === "INPUT" || target.tagName === "TEXTAREA" || target.getAttribute("role") === "textbox";
}

function onKeydown(event: KeyboardEvent): void {
  if (isTextField(event.target) || isTextField(document.activeElement)) lastTypedAt = Date.now();
}

/** Not while typing, not in the first seconds, and only while someone is looking at Fleet. */
function isQuietMoment(): boolean {
  const now = Date.now();
  return (
    now - mountedAt >= NOTICE_STARTUP_QUIET_MS &&
    now - lastTypedAt >= NOTICE_TYPING_QUIET_MS &&
    document.visibilityState === "visible" &&
    document.hasFocus()
  );
}

function tryOpen(): void {
  if (!nextWaiting.value || open.value) {
    stopWaiting();
    return;
  }
  if (isQuietMoment()) {
    stopWaiting();
    store.openNext();
  }
}

function stopWaiting(): void {
  clearInterval(waitTimer);
  waitTimer = undefined;
}

watch(
  () => [nextWaiting.value, open.value?.id] as const,
  ([waiting, openId]) => {
    if (!waiting || openId || waitTimer) return;
    waitTimer = setInterval(tryOpen, WAIT_POLL_MS);
  },
  { immediate: true, flush: "sync" },
);

// ── Hold: a card the user didn't ask for settles by itself ────────────────
function startHold(): void {
  clearTimeout(holdTimer);
  holdLeft = open.value?.holdMs ?? NOTICE_HOLD_MS;
  held = false;
  holding.value = false;
  resumeHold();
}

function resumeHold(): void {
  if (!open.value || pinned.value || held) return;
  clearTimeout(holdTimer);
  holdStartedAt = Date.now();
  holdTimer = setTimeout(() => {
    if (!pinned.value) store.settle();
  }, holdLeft);
}

function pauseHold(): void {
  held = true;
  holding.value = true;
  if (holdTimer === undefined) return;
  clearTimeout(holdTimer);
  holdTimer = undefined;
  holdLeft = Math.max(1000, holdLeft - (Date.now() - holdStartedAt));
}

function release(): void {
  if (card.value?.matches(":hover") || card.value?.contains(document.activeElement)) return;
  held = false;
  holding.value = false;
  resumeHold();
}

watch(
  () => open.value?.id,
  (id) => {
    clearTimeout(holdTimer);
    holdTimer = undefined;
    if (id) startHold();
  },
  { flush: "sync" },
);

// A card that asks something (a confirmation step) stays until it's answered.
watch(
  pinned,
  (isPinned) => {
    if (!isPinned) return;
    clearTimeout(holdTimer);
    holdTimer = undefined;
  },
  { flush: "sync" },
);

function onFocusOut(event: FocusEvent): void {
  if (!card.value?.contains(event.relatedTarget as Node | null)) release();
}

function onCardKeydown(event: KeyboardEvent): void {
  if (event.key !== "Escape") return;
  event.stopPropagation();
  store.settle();
}

async function run(action: { run: () => void | Promise<void> }): Promise<void> {
  await action.run();
}

// ── Settling: the card folds down into its chip ───────────────────────────
function prefersReducedMotion(): boolean {
  return typeof window.matchMedia === "function" && window.matchMedia("(prefers-reduced-motion: reduce)").matches;
}

function onLeave(el: Element, done: () => void): void {
  const id = (el as HTMLElement).dataset.noticeId;
  const chip = [...document.querySelectorAll<HTMLElement>("[data-notice-chip]")].find((item) => item.dataset.noticeChip === id);
  if (!chip || prefersReducedMotion() || typeof el.animate !== "function") {
    done();
    return;
  }
  const from = el.getBoundingClientRect();
  const to = chip.getBoundingClientRect();
  const scale = Math.max(0.2, to.width / Math.max(1, from.width));
  const animation = el.animate(
    [
      { transform: "none", opacity: 1 },
      { transform: `translate(${to.left - from.left}px, ${to.bottom - from.bottom}px) scale(${scale})`, opacity: 0 },
    ],
    { duration: 320, easing: "cubic-bezier(0.4, 0, 0.2, 1)" },
  );
  animation.onfinish = done;
  animation.oncancel = done;
}

onMounted(() => {
  document.addEventListener("keydown", onKeydown, true);
  window.addEventListener("focus", tryOpen);
  document.addEventListener("visibilitychange", tryOpen);
});

onUnmounted(() => {
  document.removeEventListener("keydown", onKeydown, true);
  window.removeEventListener("focus", tryOpen);
  document.removeEventListener("visibilitychange", tryOpen);
  stopWaiting();
  clearTimeout(holdTimer);
});
</script>

<template>
  <Transition
    name="notice"
    :css="true"
    @leave="onLeave"
  >
    <div
      v-if="open"
      :key="open.id"
      ref="card"
      class="notice"
      :class="{ 'notice--warn': open.tone === 'warn' }"
      role="status"
      aria-live="polite"
      :data-notice-id="open.id"
      data-testid="notice-card"
      @mouseenter="pauseHold"
      @mouseleave="release"
      @focusin="pauseHold"
      @focusout="onFocusOut"
      @keydown="onCardKeydown"
    >
      <div class="notice__top">
        <span
          class="notice__icon"
          aria-hidden="true"
        >
          <component :is="open.icon ?? CircleArrowUp" />
        </span>
        <div class="notice__text">
          <p class="notice__title">
            {{ open.title }}
          </p>
          <p
            v-if="open.body || open.link"
            class="notice__body"
          >
            {{ open.body }}
            <a
              v-if="open.link"
              class="notice__link"
              :href="open.link.href"
              target="_blank"
              rel="noopener noreferrer"
            >{{ open.link.label }}</a>
          </p>
        </div>
      </div>
      <span
        v-if="open.countdown && !pinned"
        class="notice__countdown"
        aria-hidden="true"
      >
        <span
          class="notice__countdown-bar"
          :class="{ 'notice__countdown-bar--paused': holding }"
          :style="{ animationDuration: `${open.holdMs ?? NOTICE_HOLD_MS}ms` }"
        />
      </span>
      <div
        v-if="open.actions?.length"
        class="notice__actions"
      >
        <button
          v-for="action in open.actions"
          :key="action.label"
          type="button"
          class="notice__btn"
          :class="`notice__btn--${action.tone ?? 'quiet'}`"
          @click="run(action)"
        >
          <component
            :is="action.icon"
            v-if="action.icon"
            aria-hidden="true"
          />
          {{ action.label }}
        </button>
      </div>
    </div>
  </Transition>
</template>

<style scoped>
/* Bottom-left, beside the rail: over the foot of the session list, never over the composer. */
.notice {
  position: fixed;
  left: 56px;
  bottom: 40px;
  z-index: 55;
  width: 264px;
  padding: 12px 12px 10px;
  border: 1px solid var(--border);
  border-radius: var(--radius-panel);
  background: var(--card-bg);
  color: var(--text);
  box-shadow: 0 1px 0 color-mix(in srgb, var(--text) 4%, transparent) inset, 0 16px 36px -16px rgba(0, 0, 0, 0.4);
  transform-origin: left bottom;
}

.notice__top {
  display: flex;
  align-items: flex-start;
  gap: 10px;
}

.notice__icon {
  display: grid;
  flex-shrink: 0;
  width: 26px;
  height: 26px;
  place-items: center;
  border-radius: var(--radius-btn);
  background: var(--accent-dim);
  color: var(--accent);
}

.notice--warn .notice__icon {
  background: color-mix(in srgb, var(--status-waiting) 15%, transparent);
  color: var(--status-waiting);
}

.notice__icon svg {
  width: 15px;
  height: 15px;
}

.notice__text {
  flex: 1;
  min-width: 0;
}

.notice__title {
  margin: 0;
  font-size: 13px;
  font-weight: 600;
  line-height: 1.35;
  font-variant-numeric: tabular-nums;
}

.notice__body {
  margin: 2px 0 0;
  font-size: 12px;
  line-height: 1.45;
  color: var(--muted);
  /* A note can hold a long unbroken value (a variable, a path); it wraps rather than leave the card. */
  overflow-wrap: anywhere;
}

.notice__link {
  white-space: nowrap;
  color: var(--muted);
  text-decoration: underline;
  text-decoration-color: color-mix(in srgb, var(--muted) 45%, transparent);
  text-underline-offset: 2px;
  transition: color var(--transition);
}

.notice__link:hover {
  color: var(--text);
}

/* The time left to act (Undo), draining left to right; it pauses while the card is held. */
.notice__countdown {
  display: block;
  height: 3px;
  margin: 10px 0 2px;
  overflow: hidden;
  border-radius: 3px;
  background: var(--border);
}

.notice__countdown-bar {
  display: block;
  height: 100%;
  background: var(--accent);
  transform-origin: left;
  animation: notice-countdown linear forwards;
}

.notice__countdown-bar--paused {
  animation-play-state: paused;
}

@keyframes notice-countdown {
  from { transform: scaleX(1); }
  to { transform: scaleX(0); }
}

@media (prefers-reduced-motion: reduce) {
  .notice__countdown-bar {
    animation: none;
  }
}

.notice__actions {
  display: flex;
  align-items: center;
  gap: 6px;
  margin-top: 10px;
  padding-left: 36px;
}

.notice__btn {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  height: 26px;
  padding: 0 10px;
  border: 0;
  border-radius: calc(var(--radius-btn) - 1px);
  font: inherit;
  font-size: 12px;
  font-weight: 500;
  cursor: pointer;
  transition: background var(--transition), color var(--transition);
}

.notice__btn svg {
  width: 12px;
  height: 12px;
}

.notice__btn--primary {
  background: var(--accent);
  color: var(--primary-foreground);
}

.notice__btn--primary:hover {
  background: color-mix(in srgb, var(--accent) 88%, #000);
}

.notice__btn--danger {
  background: color-mix(in srgb, var(--error) 16%, transparent);
  color: var(--error);
}

.notice__btn--danger:hover {
  background: color-mix(in srgb, var(--error) 24%, transparent);
}

.notice__btn--quiet {
  background: transparent;
  color: var(--muted);
}

/* A lone quiet button lines up with the text above it. */
.notice__actions > .notice__btn--quiet:first-child {
  margin-left: -10px;
}

.notice__btn--quiet:hover {
  background: color-mix(in srgb, var(--text) 6%, transparent);
  color: var(--text);
}

.notice__btn:focus-visible,
.notice__link:focus-visible {
  outline: 2px solid var(--accent);
  outline-offset: 1px;
}

.notice-enter-active {
  transition: opacity 260ms cubic-bezier(0.2, 0.8, 0.2, 1), transform 260ms cubic-bezier(0.2, 0.8, 0.2, 1);
}

.notice-enter-from {
  opacity: 0;
  transform: translateY(8px) scale(0.98);
}

/* On a phone the composer fills the bottom, so the card sits under the header instead. */
@media (max-width: 768px) {
  .notice {
    top: calc(env(safe-area-inset-top, 0px) + 72px);
    right: 12px;
    bottom: auto;
    left: 12px;
    width: auto;
    transform-origin: left top;
  }

  .notice-enter-from {
    transform: translateY(-8px) scale(0.98);
  }
}

@media (prefers-reduced-motion: reduce) {
  .notice-enter-active {
    transition: none;
  }
}
</style>
