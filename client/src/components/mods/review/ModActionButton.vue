<script setup lang="ts">
import { Button } from "@/components/ui/button";

/**
 * One button for the draft card and its dialogs: Fleet's own on the desktop, the phone's `ph-btn` there (the phone's
 * stylesheet resets plain buttons, which leaves the desktop button bare).
 */
withDefaults(defineProps<{
  phone?: boolean;
  variant?: "primary" | "outline" | "ghost";
  size?: "sm" | "default";
}>(), { phone: false, variant: "outline", size: "default" });
</script>

<template>
  <button
    v-if="phone"
    type="button"
    class="ph-btn"
    :class="[`ph-btn--${variant === 'ghost' ? 'outline' : variant}`, { 'ph-btn--sm': size === 'sm' }]"
  >
    <slot />
  </button>
  <Button
    v-else
    :size="size"
    :variant="variant === 'primary' ? 'default' : variant"
  >
    <slot />
  </Button>
</template>
