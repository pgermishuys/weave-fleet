<script setup lang="ts">
import { computed } from "vue";
import { useLocation } from "@tanstack/vue-router";
import { Undo2 } from "lucide-vue-next";
import { currentToast, hideToast } from "@/composables/phone/use-phone-toast";

/**
 * The phone's current toast, as desktop's ArchiveUndoToast: inverted so it reads over anything, its action (Undo) as
 * a pill, and a bar that drains while it can still be undone. It sits above the tab bar, or above the composer where
 * there's no tab bar.
 */
const location = useLocation();
const low = computed(() => !/^\/phone(\/(new|setup))?\/?$/.test(location.value.pathname));

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
      class="ph-toast"
      :class="{ 'ph-toast--low': low }"
      role="status"
      data-testid="phone-toast"
    >
      <div class="ph-toast__row">
        <span class="ph-toast__msg">{{ currentToast.text }}</span>
        <button
          v-if="currentToast.action"
          type="button"
          class="ph-toast__act ph-press"
          data-testid="phone-toast-action"
          @click="act"
        >
          <Undo2
            v-if="currentToast.action.label === 'Undo'"
            aria-hidden="true"
          />{{ currentToast.action.label }}
        </button>
      </div>
      <div
        v-if="currentToast.action"
        class="ph-toast__drain"
        :style="{ animationDuration: `${currentToast.ms}ms` }"
      />
    </div>
  </Transition>
</template>
