<script setup lang="ts">
import { Camera, FileText, Image, MessageCircle, SquareTerminal } from "lucide-vue-next";
import BottomSheet from "@/components/phone/BottomSheet.vue";

/** What + offers, as a floating sheet: a photo or screenshot, the camera, a file from the machine, a command, a side question. */
export type PlusChoice = "photo" | "camera" | "file" | "command" | "side";

defineProps<{ open: boolean; machineName: string; supportsShell: boolean; supportsSide: boolean }>();
const emit = defineEmits<{ (event: "pick", choice: PlusChoice): void; (event: "close"): void }>();
</script>

<template>
  <BottomSheet
    :open="open"
    label="Add to the message"
    floating
    @close="emit('close')"
  >
    <div class="ph-group pps__first">
      <button
        type="button"
        class="ph-row"
        style="--ph-sep-left: 56px"
        data-testid="plus-photo"
        @click="emit('pick', 'photo')"
      >
        <span class="ph-row__icon ph-row__icon--plain"><Image
          :size="24"
          aria-hidden="true"
        /></span>
        <span class="ph-row__main"><span class="ph-row__title">Photo or screenshot</span></span>
        <span class="ph-row__value">up to 5</span>
      </button>
      <button
        type="button"
        class="ph-row"
        style="--ph-sep-left: 56px"
        @click="emit('pick', 'camera')"
      >
        <span class="ph-row__icon ph-row__icon--plain"><Camera
          :size="24"
          aria-hidden="true"
        /></span>
        <span class="ph-row__main"><span class="ph-row__title">Camera</span></span>
      </button>
      <button
        type="button"
        class="ph-row"
        style="--ph-sep-left: 56px"
        data-testid="plus-file"
        @click="emit('pick', 'file')"
      >
        <span class="ph-row__icon ph-row__icon--plain"><FileText
          :size="24"
          aria-hidden="true"
        /></span>
        <span class="ph-row__main"><span class="ph-row__title">A file from {{ machineName }}</span></span>
      </button>
    </div>
    <div
      v-if="supportsShell || supportsSide"
      class="ph-group"
    >
      <button
        v-if="supportsShell"
        type="button"
        class="ph-row"
        style="--ph-sep-left: 56px"
        data-testid="plus-command"
        @click="emit('pick', 'command')"
      >
        <span class="ph-row__icon ph-row__icon--plain"><SquareTerminal
          :size="24"
          aria-hidden="true"
        /></span>
        <span class="ph-row__main"><span class="ph-row__title">Run a command</span></span>
        <span class="ph-row__value">!</span>
      </button>
      <button
        v-if="supportsSide"
        type="button"
        class="ph-row"
        style="--ph-sep-left: 56px"
        data-testid="plus-side"
        @click="emit('pick', 'side')"
      >
        <span class="ph-row__icon ph-row__icon--plain"><MessageCircle
          :size="24"
          aria-hidden="true"
        /></span>
        <span class="ph-row__main"><span class="ph-row__title">Side question</span></span>
        <span class="ph-row__value">/btw</span>
      </button>
    </div>
  </BottomSheet>
</template>

<style scoped>
.pps__first {
  margin-top: 4px;
}
</style>
