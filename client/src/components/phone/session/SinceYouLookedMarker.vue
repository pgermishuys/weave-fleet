<script setup lang="ts">
import { clock } from "@/lib/phone/time";

/** "Since you looked": where what's new starts (the time you last looked is in its title). */
defineProps<{ at: number }>();
</script>

<template>
  <div
    class="ph-since"
    role="separator"
    :aria-label="`Since you looked at ${clock(at)}`"
    :title="`Since you looked at ${clock(at)}`"
    data-testid="since-you-looked"
  >
    <span>Since you looked</span>
  </div>
</template>
