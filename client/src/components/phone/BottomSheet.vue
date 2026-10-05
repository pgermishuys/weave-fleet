<script setup lang="ts">
import { computed, nextTick, onUnmounted, shallowRef, useSlots, useTemplateRef, watch } from "vue";
import { X } from "lucide-vue-next";
import { cssPx, reducedMotion } from "@/lib/phone/animate";
import { addSample, clamp, detentOffsets, rubber, sheetRelease, velocity, type Detent, type Sample } from "@/lib/phone/gestures";
import { keyboardHeight, pageRecessed, phoneLook } from "@/composables/phone/use-phone-env";
import { popSheetEntry, pushSheetEntry, topSheetEntry } from "@/lib/phone/sheet-history";

/**
 * A sheet from the bottom of the phone screen, as the phones draw them (ported from the mockups' kit.js): a grabber,
 * detents (sized to its content, or medium and large), dragged with the finger and flicked closed with speed,
 * rubber-banding past the top, over a dimmed page. On iPhone a full sheet pushes the page back (recess) and a menu
 * floats. Tapping the dim, Escape, Android's back gesture, or the × closes it. It sits in the app frame, which
 * follows the visual viewport, so its footer (a Start button) stays above the keyboard.
 */
const props = withDefaults(defineProps<{
  open: boolean;
  label: string;
  /** A centred title in the sheet's head, with a × to close. */
  title?: string;
  detents?: Detent[];
  initial?: Detent;
  /** iOS: push the page back behind the sheet, as a card. */
  recess?: boolean;
  /** iOS: float the sheet off the screen's edges, as a menu. */
  floating?: boolean;
  /** Shorthand for a large sheet. */
  full?: boolean;
  closeButton?: boolean;
  /** Take a history entry while open, so Back closes it (off for sheets that are a route of their own). */
  history?: boolean;
  /** The default slot fills the sheet under the grabber, without the head and the scrolling body. */
  bare?: boolean;
}>(), { title: undefined, detents: undefined, initial: undefined, closeButton: undefined, history: true });
const emit = defineEmits<{ (event: "close"): void }>();
const slots = useSlots();

const layerRef = useTemplateRef<HTMLElement>("layer");
const sheetRef = useTemplateRef<HTMLElement>("sheet");
const backdropRef = useTemplateRef<HTMLElement>("backdrop");
const rendered = shallowRef(false);

const activeDetents = computed<Detent[]>(() => (props.full ? ["large"] : props.detents ?? ["fit"]));
const recess = computed(() => props.recess === true && phoneLook.value === "ios");
const floating = computed(() => props.floating === true && phoneLook.value === "ios");
const showHead = computed(() => !props.bare && (Boolean(slots.head) || Boolean(props.title) || props.closeButton === true));
const showClose = computed(() => props.closeButton ?? Boolean(props.title));

let sheetHeight = 0;
let offsets: Partial<Record<Detent, number>> = {};
let current = 0;
let entry = 0;
let generation = 0;

const stage = (): HTMLElement | null => document.querySelector<HTMLElement>(".ph-stage");
const screenHeight = (): number => layerRef.value?.clientHeight ?? window.innerHeight;
const floatGap = (): number => (floating.value && sheetRef.value ? parseFloat(getComputedStyle(sheetRef.value).bottom) || 8 : 0);
const stops = (): number[] => Object.values(offsets).sort((a, b) => a - b);
const lowest = (): number => Math.max(0, ...stops());
const dismissAt = (): number => sheetHeight + floatGap() + 10;

function layout(): void {
  const sheet = sheetRef.value;
  if (!sheet) return;
  const max = screenHeight() - cssPx("var(--ph-safe-top)") - (recess.value ? 14 : 10) - floatGap();
  if (activeDetents.value.includes("fit")) {
    sheet.style.height = "auto";
    sheet.style.maxHeight = `${max}px`;
    sheetHeight = sheet.offsetHeight;
  } else {
    sheetHeight = max;
    sheet.style.height = `${max}px`;
    sheet.style.maxHeight = "";
  }
  offsets = detentOffsets(activeDetents.value, sheetHeight, screenHeight());
}

function paint(y: number): void {
  const sheet = sheetRef.value;
  if (!sheet) return;
  current = y;
  sheet.style.transform = `translateY(${y}px)`;
  const low = lowest();
  if (backdropRef.value) backdropRef.value.style.opacity = String(y <= low ? 1 : clamp(1 - (y - low) / (dismissAt() - low), 0, 1));
  if (recess.value) {
    const r = clamp(1 - y / Math.max(1, sheetHeight), 0, 1);
    const page = stage();
    if (page) {
      page.style.transform = `translateY(${(cssPx("var(--ph-safe-top)") + 6) * r}px) scale(${1 - 0.07 * r})`;
      page.style.borderRadius = `${14 * r}px`;
    }
    pageRecessed.value = r;
  }
  sheet.classList.toggle("ph-sheet--below-top", y > (stops()[0] ?? 0) + 1);
}

async function snap(y: number, ms = 420, ease = "var(--ph-ease)"): Promise<void> {
  const sheet = sheetRef.value;
  if (!sheet) return;
  const duration = reducedMotion() ? 140 : ms;
  const transition = `transform ${duration}ms ${ease}`;
  sheet.style.transition = transition;
  if (backdropRef.value) backdropRef.value.style.transition = `opacity ${duration}ms ${ease}`;
  const page = recess.value ? stage() : null;
  if (page) page.style.transition = `${transition}, border-radius ${duration}ms ${ease}`;
  paint(y);
  await new Promise((resolve) => setTimeout(resolve, duration + 10));
  if (sheetRef.value) sheetRef.value.style.transition = "";
  if (backdropRef.value) backdropRef.value.style.transition = "";
  if (page) page.style.transition = "";
}

function resetPage(): void {
  if (!recess.value && pageRecessed.value === 0) return;
  const page = stage();
  if (page) {
    page.style.transform = "";
    page.style.borderRadius = "";
  }
  pageRecessed.value = 0;
}

async function show(): Promise<void> {
  const mine = ++generation;
  rendered.value = true;
  await nextTick();
  await new Promise((resolve) => requestAnimationFrame(resolve));
  if (mine !== generation || !sheetRef.value) return;
  layout();
  paint(dismissAt());
  void sheetRef.value.offsetWidth;
  if (props.history && !entry) entry = pushSheetEntry();
  await snap(offsets[props.initial ?? activeDetents.value[0]] ?? 0, 480);
}

async function hide(): Promise<void> {
  const mine = ++generation;
  if (entry) {
    popSheetEntry(entry);
    entry = 0;
  }
  if (!rendered.value) return;
  (document.activeElement as HTMLElement | null)?.blur?.();
  if (sheetRef.value) await snap(dismissAt(), 320, "cubic-bezier(0.4, 0, 1, 1)");
  if (mine !== generation) return;
  resetPage();
  rendered.value = false;
}

/** Opens fully (large), e.g. when a field inside needs the room. */
function expand(): void {
  if (offsets.large !== undefined) void snap(offsets.large);
}

defineExpose({ expand });

watch(() => props.open, (open) => void (open ? show() : hide()), { immediate: true });

// The keyboard changes the room the sheet has: lay it out again at the same detent.
watch(keyboardHeight, () => {
  if (!rendered.value || dragging) return;
  const at = (Object.keys(offsets) as Detent[]).find((key) => offsets[key] === current) ?? activeDetents.value[0];
  layout();
  paint(offsets[at] ?? 0);
});

// Drag: the grabber and head always; the body once it's scrolled to the top and the finger goes down.
let startY = 0;
let startOffset = 0;
let decided = false;
let dragging = false;
let samples: Sample[] = [];

function onTouchStart(event: TouchEvent): void {
  const target = event.target as Element;
  if (target.closest("input, textarea, select")) {
    decided = true;
    dragging = false;
    return;
  }
  if (activeDetents.value.includes("fit") && sheetRef.value) sheetHeight = sheetRef.value.offsetHeight;
  startY = event.touches[0].clientY;
  startOffset = current;
  decided = false;
  dragging = false;
  samples = [[performance.now(), startY]];
  if (sheetRef.value) sheetRef.value.style.transition = "";
}

function onTouchMove(event: TouchEvent): void {
  const y = event.touches[0].clientY;
  const dy = y - startY;
  if (!decided) {
    if (dy === 0) return;
    decided = true;
    const target = event.target as Element;
    const body = target.closest(".ph-sheet__body");
    const atTop = current <= (stops()[0] ?? 0) + 1;
    dragging = !body || !atTop ? true : dy > 0 && body.scrollTop <= 0;
    if (target.closest(".ph-swipe-x")) dragging = false;
  }
  if (!dragging) return;
  event.preventDefault();
  samples = addSample(samples, [performance.now(), y]);
  let next = startOffset + dy;
  const top = stops()[0] ?? 0;
  if (next < top) next = top - rubber(top - next, screenHeight() * 0.4);
  paint(next);
}

function onTouchEnd(): void {
  if (!dragging) return;
  dragging = false;
  const release = sheetRelease({ stops: stops(), current, velocity: velocity(samples), dismissAt: dismissAt() });
  if (release.close) emit("close");
  else void snap(release.to, release.ms);
}

function onKey(event: KeyboardEvent): void {
  if (event.key === "Escape" && rendered.value) emit("close");
}

// Back (Android's gesture, the browser's button) went past this sheet's entry: close it.
function onPop(): void {
  if (entry && topSheetEntry() < entry) {
    entry = 0;
    emit("close");
  }
}

window.addEventListener("popstate", onPop);
document.addEventListener("keydown", onKey);
onUnmounted(() => {
  window.removeEventListener("popstate", onPop);
  document.removeEventListener("keydown", onKey);
  if (entry) popSheetEntry(entry);
  if (rendered.value) resetPage();
});
</script>

<template>
  <Teleport
    to="#ph-overlays"
    defer
  >
    <div
      v-if="rendered"
      ref="layer"
      class="ph-sheet-layer"
    >
      <div
        ref="backdrop"
        class="ph-sheet-backdrop"
        aria-hidden="true"
        @click="emit('close')"
      />
      <section
        ref="sheet"
        class="ph-sheet"
        :class="{ 'ph-sheet--floating': floating, 'ph-sheet--has-foot': Boolean($slots.foot) }"
        role="dialog"
        aria-modal="true"
        :aria-label="label"
        @touchstart.passive="onTouchStart"
        @touchmove="onTouchMove"
        @touchend="onTouchEnd"
        @touchcancel="onTouchEnd"
      >
        <div
          class="ph-sheet__grabber"
          aria-hidden="true"
        />
        <slot v-if="bare" />
        <template v-else>
          <div
            v-if="showHead"
            class="ph-sheet__head"
          >
            <slot name="head">
              <h2>{{ title }}</h2>
              <span class="ph-navbar__spacer" />
              <button
                v-if="showClose"
                type="button"
                class="ph-navbtn ph-glass"
                aria-label="Close"
                data-testid="sheet-close"
                @click="emit('close')"
              >
                <X
                  :size="22"
                  :stroke-width="2.4"
                  aria-hidden="true"
                />
              </button>
            </slot>
          </div>
          <div class="ph-sheet__body">
            <slot />
          </div>
          <div
            v-if="$slots.foot"
            class="ph-sheet__foot"
          >
            <slot name="foot" />
          </div>
        </template>
      </section>
    </div>
  </Teleport>
</template>
