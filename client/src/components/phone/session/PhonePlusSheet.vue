<script setup lang="ts">
import { Camera, FileText, Image, MessageCircleQuestionMark, SquareTerminal } from "lucide-vue-next";
import BottomSheet from "@/components/phone/BottomSheet.vue";

/** What + offers: a photo or screenshot, the camera, a file from the machine, a command, a side question. */
export type PlusChoice = "photo" | "camera" | "file" | "command" | "side";

defineProps<{ open: boolean; machineName: string; supportsShell: boolean; supportsSide: boolean }>();
const emit = defineEmits<{ (event: "pick", choice: PlusChoice): void; (event: "close"): void }>();
</script>

<template>
  <BottomSheet
    :open="open"
    label="Add to the message"
    @close="emit('close')"
  >
    <button
      type="button"
      class="pps__row"
      data-testid="plus-photo"
      @click="emit('pick', 'photo')"
    >
      <Image
        :size="18"
        aria-hidden="true"
      /><span>Photo or screenshot <span class="pps__hint">up to 5</span></span>
    </button>
    <button
      type="button"
      class="pps__row"
      @click="emit('pick', 'camera')"
    >
      <Camera
        :size="18"
        aria-hidden="true"
      /><span>Camera</span>
    </button>
    <button
      type="button"
      class="pps__row"
      data-testid="plus-file"
      @click="emit('pick', 'file')"
    >
      <FileText
        :size="18"
        aria-hidden="true"
      /><span>A file from {{ machineName }}</span>
    </button>
    <button
      v-if="supportsShell"
      type="button"
      class="pps__row"
      data-testid="plus-command"
      @click="emit('pick', 'command')"
    >
      <SquareTerminal
        :size="18"
        aria-hidden="true"
      /><span>Run a command <span class="pps__hint">no model turn</span></span>
    </button>
    <button
      v-if="supportsSide"
      type="button"
      class="pps__row"
      data-testid="plus-side"
      @click="emit('pick', 'side')"
    >
      <MessageCircleQuestionMark
        :size="18"
        aria-hidden="true"
      /><span>Side question <span class="pps__hint">/btw, doesn't touch this session</span></span>
    </button>
  </BottomSheet>
</template>

<style scoped>
.pps__row {
  display: flex;
  align-items: center;
  gap: 12px;
  width: 100%;
  min-height: 52px;
  padding: 6px 4px;
  border: 0;
  border-bottom: 1px solid var(--border);
  background: transparent;
  color: var(--text);
  font: inherit;
  font-size: 15px;
  text-align: left;
}

.pps__hint {
  margin-left: 4px;
  font-size: 12px;
  color: var(--muted);
}
</style>
