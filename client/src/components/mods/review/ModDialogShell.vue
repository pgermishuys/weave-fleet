<script setup lang="ts">
import BottomSheet from "@/components/phone/BottomSheet.vue";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";

/**
 * A dialog on the desktop and the phone's sheet on the phone, with a footer that stays in view while the body scrolls.
 */
defineProps<{
  open: boolean;
  /** Plain text: the sheet's title and the accessible name. The `title` slot may dress it up on the desktop. */
  label: string;
  description?: string;
  phone?: boolean;
  /** Classes for the desktop dialog (its width). */
  contentClass?: string;
  testid?: string;
}>();

const emit = defineEmits<{ "update:open": [value: boolean] }>();
</script>

<template>
  <BottomSheet
    v-if="phone"
    :open="open"
    :label="label"
    :title="label"
    :subtitle="description"
    @close="emit('update:open', false)"
  >
    <slot />
    <template
      v-if="$slots.foot"
      #foot
    >
      <slot name="foot" />
    </template>
  </BottomSheet>
  <Dialog
    v-else
    :open="open"
    @update:open="(value) => emit('update:open', value)"
  >
    <DialogContent
      :class="['rounded-panel flex max-h-[88dvh] w-[calc(100vw-1rem)] flex-col gap-3 overflow-hidden', contentClass]"
      :data-testid="testid"
    >
      <DialogHeader>
        <DialogTitle>
          <slot name="title">
            {{ label }}
          </slot>
        </DialogTitle>
        <DialogDescription v-if="description">
          {{ description }}
        </DialogDescription>
      </DialogHeader>
      <div class="min-h-0 flex-1 space-y-3 overflow-y-auto">
        <slot />
      </div>
      <div
        v-if="$slots.foot"
        class="flex-none space-y-2"
      >
        <slot name="foot" />
      </div>
    </DialogContent>
  </Dialog>
</template>
