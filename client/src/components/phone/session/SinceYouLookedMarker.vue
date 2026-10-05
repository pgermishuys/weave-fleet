<script setup lang="ts">
import { clock } from "@/lib/phone/time";

/** "Since you looked at 14:20": where what's new starts. */
defineProps<{ at: number }>();
</script>

<template>
  <div
    class="syl"
    role="separator"
    data-testid="since-you-looked"
  >
    <span>Since you looked at {{ clock(at) }}</span>
  </div>
</template>

<style scoped>
.syl {
  display: flex;
  align-items: center;
  gap: 10px;
  margin: 4px 0;
  font-size: 11px;
  font-weight: 600;
  color: var(--accent);
}

.syl::before,
.syl::after {
  content: "";
  flex: 1;
  height: 1px;
  background: color-mix(in srgb, var(--accent) 40%, transparent);
}
</style>
