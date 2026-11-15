<script setup lang="ts">
import { formatTime } from "@/lib/format";

/** "Since you looked": where what's new starts (the time you last looked is in its title). */
defineProps<{ at: number }>();
</script>

<template>
  <div
    class="ph-since"
    role="separator"
    :aria-label="`Since you looked at ${formatTime(at)}`"
    :title="`Since you looked at ${formatTime(at)}`"
    data-testid="since-you-looked"
  >
    <span>Since you looked</span>
  </div>
</template>
