<script setup lang="ts">
import { nextTick, onBeforeUnmount, useTemplateRef, watch } from "vue";
import { X } from "lucide-vue-next";

/** An image over the page at the largest size that fits. Esc, the backdrop or the close button put it away. */
const props = withDefaults(defineProps<{
  /** The image to show; null keeps the lightbox closed. */
  src: string | null;
  alt?: string;
}>(), {
  alt: "Image preview",
});

const emit = defineEmits<{
  close: [];
}>();

const closeButton = useTemplateRef<HTMLButtonElement>("closeButton");
// Where focus was when it opened, so closing hands it back (the thumbnail, for a keyboard user).
let returnFocusTo: HTMLElement | null = null;

function close(): void {
  emit("close");
}

function handleKeydown(event: KeyboardEvent): void {
  if (event.key !== "Escape") return;
  event.preventDefault();
  event.stopPropagation();
  close();
}

watch(
  () => props.src,
  async (src, previous) => {
    if (src && !previous) {
      returnFocusTo = document.activeElement instanceof HTMLElement ? document.activeElement : null;
      document.addEventListener("keydown", handleKeydown, true);
      await nextTick();
      closeButton.value?.focus();
    } else if (!src && previous) {
      document.removeEventListener("keydown", handleKeydown, true);
      returnFocusTo?.focus();
      returnFocusTo = null;
    }
  },
  { immediate: true },
);

onBeforeUnmount(() => document.removeEventListener("keydown", handleKeydown, true));
</script>

<template>
  <Teleport to="body">
    <div
      v-if="src"
      class="lightbox-overlay"
      role="dialog"
      aria-modal="true"
      :aria-label="alt"
      data-testid="image-lightbox"
      @click="close"
    >
      <img
        :src="src"
        :alt="alt"
        class="lightbox-image"
        data-testid="image-lightbox-image"
        @click.stop
      >
      <button
        ref="closeButton"
        type="button"
        class="lightbox-close"
        title="Close preview"
        aria-label="Close preview"
        data-testid="image-lightbox-close"
        @click="close"
      >
        <X
          class="lightbox-close__icon"
          aria-hidden="true"
        />
      </button>
    </div>
  </Teleport>
</template>

<style scoped>
.lightbox-overlay {
  position: fixed;
  inset: 0;
  z-index: 9999;
  display: flex;
  align-items: center;
  justify-content: center;
  padding: 16px;
  background: rgba(0, 0, 0, 0.8);
  cursor: pointer;
}

.lightbox-image {
  max-width: min(90vw, 100%);
  max-height: 90vh;
  border-radius: 0;
  object-fit: contain;
  box-shadow: 0 8px 32px rgba(0, 0, 0, 0.5);
  cursor: default;
}

.lightbox-close {
  position: absolute;
  top: 16px;
  right: 16px;
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 36px;
  height: 36px;
  padding: 0;
  border: none;
  border-radius: 50%;
  background: rgba(255, 255, 255, 0.15);
  color: #fff;
  cursor: pointer;
}

.lightbox-close:hover {
  background: rgba(255, 255, 255, 0.25);
}

.lightbox-close:focus-visible {
  outline: 2px solid #fff;
  outline-offset: 2px;
}

.lightbox-close__icon {
  width: 20px;
  height: 20px;
}
</style>
