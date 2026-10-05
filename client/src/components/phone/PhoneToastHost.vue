<script setup lang="ts">
import { currentToast, hideToast } from "@/composables/phone/use-phone-toast";

/** Shows the phone's current toast, with its action (Undo) when it has one. */
function act(): void {
  const action = currentToast.value?.action;
  hideToast();
  action?.run();
}
</script>

<template>
  <Transition name="ph-toast">
    <div
      v-if="currentToast"
      :key="currentToast.id"
      class="ph-toast ph-glass"
      :class="{ 'ph-toast--plain': !currentToast.action }"
      role="status"
      data-testid="phone-toast"
    >
      <span>{{ currentToast.text }}</span>
      <button
        v-if="currentToast.action"
        type="button"
        class="ph-press"
        @click="act"
      >
        {{ currentToast.action.label }}
      </button>
    </div>
  </Transition>
</template>
