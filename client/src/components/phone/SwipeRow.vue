<script lang="ts">
/** The row left open, if any: only one is open at a time. */
const opened: { row: { close: () => void } | null } = { row: null };
</script>

<script setup lang="ts">
import { onUnmounted, useTemplateRef } from "vue";
import { Archive } from "lucide-vue-next";
import { animateTo, collapse } from "@/lib/phone/animate";
import { addSample, axisIntent, swipeRowRelease, velocity, type Sample } from "@/lib/phone/gestures";

/**
 * A list row you can swipe left to archive, as Mail does: Archive shows behind it, a short swipe leaves it open, all
 * the way archives it (the row slides off and folds away). Only ever archive: nothing is approved with a swipe.
 */
const emit = defineEmits<{ (event: "archive"): void }>();

const wrapRef = useTemplateRef<HTMLElement>("wrap");
const ACTION = 88;
let startX = 0;
let startY = 0;
let base = 0;
let x = 0;
let mode: "maybe" | "drag" | "no" | null = null;
let samples: Sample[] = [];

const self = { close };

function row(): HTMLElement | null {
  return wrapRef.value?.querySelector<HTMLElement>(":scope > .ph-row") ?? null;
}
function actions(): HTMLElement | null {
  return wrapRef.value?.querySelector<HTMLElement>(":scope > .ph-swipe-actions") ?? null;
}

function set(value: number): void {
  x = value;
  const el = row();
  const acts = actions();
  if (el) el.style.transform = `translateX(${value}px)`;
  if (acts) {
    acts.style.opacity = value < 0 ? "1" : "0";
    const button = acts.firstElementChild as HTMLElement | null;
    if (button) button.style.width = `${Math.max(ACTION, -value)}px`;
  }
}

function close(): void {
  const el = row();
  x = 0;
  if (el) void animateTo(el, { transform: "translateX(0)" }, 320).then(() => {
    const acts = actions();
    if (acts && x === 0) acts.style.opacity = "0";
  });
  if (opened.row === self) opened.row = null;
}

async function commit(): Promise<void> {
  const wrap = wrapRef.value;
  const el = row();
  if (!wrap || !el) return;
  opened.row = null;
  await animateTo(el, { transform: `translateX(${-wrap.clientWidth}px)` }, 220, "var(--ph-ease-out)");
  const button = actions()?.firstElementChild as HTMLElement | null;
  if (button) button.style.width = "100%";
  await collapse(wrap, 260);
  emit("archive");
}

function onTouchStart(event: TouchEvent): void {
  if (opened.row && opened.row !== self) opened.row.close();
  const touch = event.touches[0];
  startX = touch.clientX;
  startY = touch.clientY;
  base = x;
  mode = "maybe";
  samples = [[performance.now(), startX]];
}

function onTouchMove(event: TouchEvent): void {
  if (!mode || mode === "no") return;
  const touch = event.touches[0];
  const dx = touch.clientX - startX;
  if (mode === "maybe") {
    const intent = axisIntent(dx, touch.clientY - startY, 1.2);
    if (!intent) return;
    mode = intent === "x" && (dx < 0 || base < 0) ? "drag" : "no";
    if (mode === "no") return;
    row()?.classList.remove("is-pressed");
  }
  event.preventDefault();
  event.stopPropagation();
  samples = addSample(samples, [performance.now(), touch.clientX]);
  set(Math.min(0, base + dx));
}

function onTouchEnd(): void {
  if (mode !== "drag") {
    mode = null;
    return;
  }
  mode = null;
  const outcome = swipeRowRelease({ x, width: wrapRef.value?.clientWidth ?? 390, velocity: velocity(samples), actionWidth: ACTION });
  if (outcome === "commit") {
    void commit();
    return;
  }
  if (outcome === "open") {
    const el = row();
    if (el) void animateTo(el, { transform: `translateX(${-ACTION}px)` }, 300);
    x = -ACTION;
    opened.row = self;
    return;
  }
  close();
}

// A tap on a row left open closes it rather than opening the session.
function onClickCapture(event: MouseEvent): void {
  if (x === 0 || (event.target as Element).closest(".ph-swipe-act")) return;
  event.preventDefault();
  event.stopPropagation();
  close();
}

function onOutside(event: TouchEvent): void {
  if (opened.row === self && !wrapRef.value?.contains(event.target as Node)) close();
}
document.addEventListener("touchstart", onOutside, { passive: true });
onUnmounted(() => {
  document.removeEventListener("touchstart", onOutside);
  if (opened.row === self) opened.row = null;
});
</script>

<template>
  <div
    ref="wrap"
    class="ph-row-wrap ph-swipe-x ph-no-swipe-back"
    @touchstart.passive="onTouchStart"
    @touchmove="onTouchMove"
    @touchend="onTouchEnd"
    @touchcancel="onTouchEnd"
    @click.capture="onClickCapture"
  >
    <div class="ph-swipe-actions">
      <button
        type="button"
        class="ph-swipe-act"
        aria-label="Archive"
        tabindex="-1"
        @click="commit"
      >
        <Archive
          :size="22"
          aria-hidden="true"
        />
        <span>Archive</span>
      </button>
    </div>
    <slot />
  </div>
</template>
