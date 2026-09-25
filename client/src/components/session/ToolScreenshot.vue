<script setup lang="ts">
import { shallowRef } from "vue";
import ImageLightbox from "@/components/session/ImageLightbox.vue";
import type { ToolCardScreenshot } from "@/components/session/activity-stream-tool-card";

/** What the agent saw when it took a screenshot: a small picture under the call that opens full size. */
const props = defineProps<{
  screenshot: ToolCardScreenshot;
  /** The call's title ("Shop · 1280×800"), which names the picture. */
  title: string;
}>();

const expanded = shallowRef(false);
// A shot whose session was deleted, or kept by a Fleet that has since lost it: show nothing rather than a broken image.
const missing = shallowRef(false);
</script>

<template>
  <div
    v-if="!missing"
    class="tool-screenshot"
  >
    <button
      type="button"
      class="tool-screenshot__thumb"
      :title="`Open screenshot: ${props.title}`"
      :aria-label="`Open screenshot: ${props.title}`"
      data-testid="tool-screenshot"
      @click="expanded = true"
    >
      <img
        :src="screenshot.url"
        :width="screenshot.width"
        :height="screenshot.height"
        :alt="`Screenshot: ${props.title}`"
        class="tool-screenshot__img"
        loading="lazy"
        decoding="async"
        @error="missing = true"
      >
    </button>
    <ImageLightbox
      :src="expanded ? screenshot.url : null"
      :alt="`Screenshot: ${props.title}`"
      @close="expanded = false"
    />
  </div>
</template>

<style scoped>
/* Lines up with the call's label above it, like the card's body. */
.tool-screenshot {
  padding: 2px 8px 8px 31px;
}

.tool-screenshot__thumb {
  display: block;
  max-width: 100%;
  padding: 0;
  border: 1px solid var(--border);
  border-radius: var(--radius-btn);
  background: var(--main-bg);
  cursor: zoom-in;
  overflow: hidden;
  transition: border-color var(--transition);
}

.tool-screenshot__thumb:hover {
  border-color: color-mix(in srgb, var(--text) 30%, transparent);
}

.tool-screenshot__thumb:focus-visible {
  outline: 2px solid var(--accent);
  outline-offset: 2px;
}

/* A fixed height keeps a desktop shot and a phone shot the same size in the stream; the width follows the shape. */
.tool-screenshot__img {
  display: block;
  width: auto;
  max-width: 100%;
  height: 120px;
  object-fit: cover;
  object-position: top left;
}
</style>
