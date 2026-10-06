<script setup lang="ts">
import { Camera, Image, MessageCircle, Paperclip, Terminal } from "lucide-vue-next";
import BottomSheet from "@/components/phone/BottomSheet.vue";

/** What + offers, as a floating menu: a photo or screenshot, the camera, a file from the folder, a command, a side question. */
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
    <div
      class="ph-menu"
      role="menu"
    >
      <button
        type="button"
        class="ph-mi"
        role="menuitem"
        data-testid="plus-photo"
        @click="emit('pick', 'photo')"
      >
        <Image aria-hidden="true" />
        <span>Photo or screenshot</span>
        <span class="ph-mi__hint">up to 5</span>
      </button>
      <button
        type="button"
        class="ph-mi"
        role="menuitem"
        @click="emit('pick', 'camera')"
      >
        <Camera aria-hidden="true" />
        <span>Camera</span>
      </button>
      <button
        type="button"
        class="ph-mi"
        role="menuitem"
        data-testid="plus-file"
        @click="emit('pick', 'file')"
      >
        <Paperclip aria-hidden="true" />
        <span>A file from {{ machineName }}</span>
        <span class="ph-mi__hint">@</span>
      </button>
      <template v-if="supportsShell || supportsSide">
        <hr>
        <button
          v-if="supportsShell"
          type="button"
          class="ph-mi"
          role="menuitem"
          data-testid="plus-command"
          @click="emit('pick', 'command')"
        >
          <Terminal aria-hidden="true" />
          <span>Run a command</span>
          <span class="ph-mi__hint">!</span>
        </button>
        <button
          v-if="supportsSide"
          type="button"
          class="ph-mi"
          role="menuitem"
          data-testid="plus-side"
          @click="emit('pick', 'side')"
        >
          <MessageCircle aria-hidden="true" />
          <span>Side question</span>
          <span class="ph-mi__hint">/btw</span>
        </button>
      </template>
    </div>
  </BottomSheet>
</template>
