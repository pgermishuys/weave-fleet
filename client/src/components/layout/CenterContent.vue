<script setup lang="ts">
import { computed } from "vue";
import { useLocation } from "@tanstack/vue-router";

const location = useLocation();
const noPadding = computed(() => location.value.pathname.startsWith("/sessions/"));
</script>

<template>
  <main class="center">
    <div class="center__body" :class="{ 'center__body--no-padding': noPadding }">
      <slot />
    </div>
  </main>
</template>

<style scoped>
.center {
  flex: 1;
  display: flex;
  flex-direction: column;
  overflow: hidden;
}

.center__placeholder {
  padding: 12px 24px;
  border-bottom: 1px solid var(--border);
  font-size: 10px;
  font-weight: 600;
  letter-spacing: 0.05em;
  text-transform: uppercase;
  color: var(--muted);
}

.center__body {
  flex: 1;
  min-height: 0;
  overflow: auto;
  padding: 24px;
}

.center__body--no-padding {
  padding: 0;
  overflow: hidden;
}
</style>
