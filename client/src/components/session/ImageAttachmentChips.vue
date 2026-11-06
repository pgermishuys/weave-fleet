<script setup lang="ts">
import { shallowRef } from "vue";
import { X } from "lucide-vue-next";
import ImageLightbox from "@/components/session/ImageLightbox.vue";
import type { PendingAttachment } from "@/composables/use-draft-attachments";

/** The images held for a prompt, as thumbnails that open large and can be taken off. */
defineProps<{
  attachments: readonly PendingAttachment[];
}>();

defineEmits<{
  remove: [id: string];
}>();

const lightboxUrl = shallowRef<string | null>(null);
</script>

<template>
  <div
    v-for="att in attachments"
    :key="att.id"
    class="attachment-chip"
    data-testid="image-attachment-chip"
  >
    <button
      type="button"
      class="attachment-chip__thumb-btn"
      title="Click to preview"
      @click="lightboxUrl = att.previewUrl"
    >
      <img
        :src="att.previewUrl"
        :alt="att.filename ?? 'image'"
        class="attachment-chip__thumb"
      >
    </button>
    <span class="attachment-chip__name">{{ att.filename ?? 'image.png' }}</span>
    <button
      type="button"
      class="attachment-chip__remove"
      :title="`Remove ${att.filename ?? 'image'}`"
      @click="$emit('remove', att.id)"
    >
      <X class="attachment-chip__remove-icon" />
    </button>
  </div>
  <ImageLightbox
    :src="lightboxUrl"
    @close="lightboxUrl = null"
  />
</template>

<style scoped>
.attachment-chip {
  display: inline-flex;
  flex-direction: column;
  align-items: center;
  gap: 4px;
  padding: 6px;
  border: 1px solid var(--border);
  border-radius: var(--radius-btn);
  background: var(--panel-bg);
  font-size: 11px;
  color: var(--text);
  position: relative;
}

.attachment-chip__thumb-btn {
  display: inline-flex;
  padding: 0;
  border: none;
  background: transparent;
  cursor: pointer;
  border-radius: calc(var(--radius-btn) - 3px);
}

.attachment-chip__thumb-btn:hover {
  opacity: 0.8;
}

.attachment-chip__thumb {
  width: 80px;
  height: 80px;
  border-radius: calc(var(--radius-btn) - 3px);
  object-fit: cover;
}

.attachment-chip__name {
  max-width: 80px;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  color: var(--muted);
  font-size: 10px;
}

.attachment-chip__remove {
  position: absolute;
  top: 4px;
  right: 4px;
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 18px;
  height: 18px;
  padding: 0;
  border: none;
  border-radius: 50%;
  background: rgba(0, 0, 0, 0.6);
  color: #fff;
  cursor: pointer;
  opacity: 0;
  transition: opacity var(--transition);
}

.attachment-chip:hover .attachment-chip__remove {
  opacity: 1;
}

.attachment-chip__remove:hover {
  background: rgba(220, 38, 38, 0.8);
}

.attachment-chip__remove-icon {
  width: 12px;
  height: 12px;
}
</style>
