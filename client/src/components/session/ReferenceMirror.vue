<script setup lang="ts">
/**
 * The draft's `@` references (and `$` skills) tinted where they sit: this sits behind the (transparent) text area with
 * the same text laid out the same way. Put it just before the text area, both inside a `position: relative` box with
 * the text area `position: relative` too, and give it any classes the text area has besides `composer-frame__textarea`.
 * Call `sync` after resizing the text area and when it scrolls. Both composers draw their drafts with it;
 * `useDraftReferences` makes its segments.
 */
import { onUnmounted, useTemplateRef, watch } from "vue";
import type { DraftSegment } from "@/lib/composer-references";

const props = defineProps<{
  segments: readonly DraftSegment[];
  textarea: HTMLTextAreaElement | null;
  testId?: string;
}>();

const mirrorRef = useTemplateRef<HTMLDivElement>("mirror");
let resizeObserver: ResizeObserver | null = null;

/** Keeps the mirror's height, line width and scroll in step with the text area's. */
function sync(): void {
  const textarea = props.textarea;
  const mirror = mirrorRef.value;
  if (!textarea || !mirror) {
    return;
  }

  mirror.style.height = `${textarea.offsetHeight}px`;
  // A scrollbar narrows the text area's lines; narrow the mirror's to match.
  const scrollbarWidth = textarea.offsetWidth - textarea.clientWidth;
  mirror.style.paddingRight = `${parseFloat(getComputedStyle(textarea).paddingRight) + scrollbarWidth}px`;
  mirror.scrollTop = textarea.scrollTop;
}

watch(() => props.textarea, (textarea) => {
  resizeObserver?.disconnect();
  resizeObserver = null;
  sync();
  if (textarea && typeof ResizeObserver !== "undefined") {
    resizeObserver = new ResizeObserver(() => sync());
    resizeObserver.observe(textarea);
  }
}, { immediate: true, flush: "post" });

onUnmounted(() => {
  resizeObserver?.disconnect();
  resizeObserver = null;
});

defineExpose({ sync });
</script>

<template>
  <!--
    No whitespace between these nodes: the mirror lays text out as the text area does. The
    trailing space gives a draft that ends in a newline its last line, as the text area has.
  -->
  <!-- eslint-disable vue/multiline-html-element-content-newline, vue/singleline-html-element-content-newline -->
  <div
    ref="mirror"
    class="composer-frame__textarea composer-input__mirror"
    :data-testid="testId"
    aria-hidden="true"
  ><template
    v-for="(segment, index) in segments"
    :key="index"
  ><span
    v-if="segment.reference"
    class="composer-reference"
    :class="{
      'composer-reference--session': segment.reference === 'session',
      'composer-reference--skill': segment.reference === 'skill' && !segment.note,
      'composer-reference--skill-note': segment.note,
    }"
  >{{ segment.text }}</span><template v-else>{{ segment.text }}</template></template>{{ " " }}</div>
  <!-- eslint-enable vue/multiline-html-element-content-newline, vue/singleline-html-element-content-newline -->
</template>

<!-- Not scoped: the composers style the mirror as their own part (a shell command's type, say). -->
<style>
/* Over the frame's text area rule, which it also carries for the same layout. */
.composer-frame__textarea.composer-input__mirror {
  position: absolute;
  top: 0;
  right: 0;
  left: 0;
  min-height: 0;
  max-height: none;
  overflow: hidden;
  color: transparent;
  white-space: pre-wrap;
  overflow-wrap: break-word;
  pointer-events: none;
  user-select: none;
}

.composer-reference {
  --reference-tint: color-mix(in srgb, var(--accent) 28%, transparent);

  border-radius: 4px;
  background: var(--reference-tint);
  /* Widen the tint past the text without moving it. */
  box-shadow: -3px 0 0 var(--reference-tint), 3px 0 0 var(--reference-tint);
  -webkit-box-decoration-break: clone;
  box-decoration-break: clone;
}

/* A session picked from the @ list: the accent, as the chip it becomes in the sent message. */
.composer-reference--session {
  --reference-tint: color-mix(in srgb, var(--accent) 18%, transparent);
}

/* A skill: green when it goes with the message, amber when it goes as a note (see skillDeliveries). */
.composer-reference--skill {
  --reference-tint: color-mix(in srgb, var(--running) 22%, transparent);
}

.composer-reference--skill-note {
  --reference-tint: color-mix(in srgb, var(--idle) 22%, transparent);
}
</style>
