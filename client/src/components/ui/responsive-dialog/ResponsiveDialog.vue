<script setup lang="ts">
import type { HTMLAttributes } from "vue";
import { useIsMobile } from "@/composables/use-media-query";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import {
  Sheet,
  SheetContent,
  SheetDescription,
  SheetHeader,
  SheetTitle,
} from "@/components/ui/sheet";

const props = defineProps<{
  open?: boolean;
  title?: string;
  description?: string;
  class?: HTMLAttributes["class"];
}>();

const emit = defineEmits<{
  "update:open": [value: boolean];
}>();

const isMobile = useIsMobile();

function handleOpenChange(value: boolean): void {
  emit("update:open", value);
}
</script>

<template>
  <!-- Mobile: bottom sheet -->
  <Sheet
    v-if="isMobile"
    :open="props.open"
    @update:open="handleOpenChange"
  >
    <SheetContent
      side="bottom"
      :class="[
        'max-h-[calc(var(--visual-vh,100dvh)*0.75)] overflow-y-auto pb-[env(safe-area-inset-bottom,0px)]',
        props.class,
      ]"
    >
      <SheetHeader v-if="props.title || props.description">
        <SheetTitle v-if="props.title">
          {{ props.title }}
        </SheetTitle>
        <SheetDescription v-if="props.description">
          {{ props.description }}
        </SheetDescription>
      </SheetHeader>
      <slot />
    </SheetContent>
  </Sheet>

  <!-- Desktop: centered dialog -->
  <Dialog
    v-else
    :open="props.open"
    @update:open="handleOpenChange"
  >
    <DialogContent :class="props.class">
      <DialogHeader v-if="props.title || props.description">
        <DialogTitle v-if="props.title">
          {{ props.title }}
        </DialogTitle>
        <DialogDescription v-if="props.description">
          {{ props.description }}
        </DialogDescription>
      </DialogHeader>
      <slot />
    </DialogContent>
  </Dialog>
</template>
