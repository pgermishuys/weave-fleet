<script setup lang="ts">
import { clock } from "@/lib/phone/time";

/** "Since you looked at 14:20": where what's new starts. */
defineProps<{ at: number }>();
</script>

<template>
  <div
    class="ph-since"
    role="separator"
    data-testid="since-you-looked"
  >
    <span>Since you looked at {{ clock(at) }}</span>
  </div>
</template>
