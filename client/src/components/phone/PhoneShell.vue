<script setup lang="ts">
/**
 * The frame for Fleet's phone pages (`/pair`, `/phone…`): full height, inside the notch and home-indicator safe
 * areas, without the desktop shell's rail and panels.
 */
</script>

<template>
  <div
    class="phone-shell"
    data-testid="phone-shell"
  >
    <slot />
  </div>
</template>

<style scoped>
.phone-shell {
  display: flex;
  flex-direction: column;
  min-height: 100dvh;
  padding: env(safe-area-inset-top) env(safe-area-inset-right) env(safe-area-inset-bottom) env(safe-area-inset-left);
  background: var(--main-bg);
  color: var(--text);
  font-family: var(--font-sans-stack);
  -webkit-text-size-adjust: 100%;
}
</style>
