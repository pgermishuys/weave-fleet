<script setup lang="ts">
import { shallowRef } from "vue";
import { LoaderCircle } from "lucide-vue-next";
import { useBoardFeature } from "@/composables/use-board-feature";
import { usePreferencesStore } from "@/stores/preferences";

const preferencesStore = usePreferencesStore();
const { isBoardFeatureEnabled, setBoardFeatureEnabled } = useBoardFeature();

const isSavingBoardFeature = shallowRef(false);
const boardFeatureError = shallowRef<string | null>(null);

async function toggleBoardFeature(): Promise<void> {
  const enabled = !isBoardFeatureEnabled.value;

  isSavingBoardFeature.value = true;
  boardFeatureError.value = null;

  try {
    await setBoardFeatureEnabled(enabled);
  } catch (error) {
    boardFeatureError.value = error instanceof Error
      ? error.message
      : "Failed to update Board feature setting.";
  } finally {
    isSavingBoardFeature.value = false;
  }
}
</script>

<template>
  <section class="rounded-card border border-border bg-card-bg p-6 shadow-sm">
    <div class="flex flex-col gap-1">
      <h2 class="text-lg font-semibold text-text">
        Features
      </h2>
      <p class="text-sm text-muted">
        Enable or hide optional workspace features.
      </p>
    </div>

    <div class="mt-5 flex items-start justify-between gap-4 rounded-card border border-border bg-main-bg p-4">
      <div>
        <p class="text-sm font-medium text-text">
          Board
        </p>
        <p class="mt-1 text-xs text-muted">
          Show the Board entry in the left rail and enable the board workspace panels.
        </p>
        <p
          v-if="boardFeatureError"
          class="mt-2 text-xs text-red-300"
          role="alert"
        >
          {{ boardFeatureError }}
        </p>
      </div>

      <div class="flex items-center gap-2">
        <LoaderCircle
          v-if="isSavingBoardFeature"
          :size="16"
          class="animate-spin text-muted"
          aria-hidden="true"
        />
        <button
          type="button"
          role="switch"
          :aria-checked="isBoardFeatureEnabled"
          :disabled="preferencesStore.isLoading || isSavingBoardFeature"
          aria-label="Enable Board feature"
          class="relative inline-flex h-6 w-11 shrink-0 cursor-pointer rounded-full border-2 border-transparent transition-colors duration-200 ease-in-out focus:outline-none focus:ring-2 focus:ring-accent focus:ring-offset-2 focus:ring-offset-main-bg disabled:cursor-not-allowed disabled:opacity-60"
          :class="isBoardFeatureEnabled ? 'bg-accent' : 'bg-border'"
          @click="toggleBoardFeature"
        >
          <span
            class="pointer-events-none inline-block h-5 w-5 rounded-full bg-white shadow ring-0 transition duration-200 ease-in-out"
            :class="isBoardFeatureEnabled ? 'translate-x-5' : 'translate-x-0'"
          />
        </button>
      </div>
    </div>
  </section>
</template>
