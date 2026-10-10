<script setup lang="ts">
import { computed } from "vue";
import { TriangleAlert } from "lucide-vue-next";
import type { ModsRuntimeJob, ModsRuntimeRelease } from "@/lib/mods-runtime";
import { PRIMARY_BUTTON, SECONDARY_BUTTON } from "./classes";

/**
 * The install failed and the switch went back off (step 5): the reason in plain words, and two ways on. Retry is
 * hidden when there is no build for this computer, since trying again can't help.
 */
const props = defineProps<{
  job: ModsRuntimeJob;
  release: ModsRuntimeRelease;
  busy?: boolean;
}>();

defineEmits<{ retry: []; own: [] }>();

const hint = computed(() => {
  switch (props.job.reason) {
    case "offline":
      return "If GitHub is blocked here, install Bun yourself and point Fleet at it.";
    case "blocked":
      return `Ask whoever runs your network to allow ${props.release.source}, or install Bun yourself and point Fleet at it.`;
    default:
      return "";
  }
});
</script>

<template>
  <section
    aria-labelledby="mods-failed-heading"
    class="space-y-3"
  >
    <h4
      id="mods-failed-heading"
      class="flex items-center gap-2 text-sm font-medium text-error"
    >
      <TriangleAlert
        :size="16"
        aria-hidden="true"
      />
      Couldn't download Bun
    </h4>
    <p class="max-w-xl text-sm leading-6 text-muted">
      {{ job.message }} {{ hint }}
    </p>
    <div class="flex flex-wrap items-center gap-2">
      <button
        v-if="job.reason !== 'no-build'"
        type="button"
        :class="PRIMARY_BUTTON"
        :disabled="busy"
        @click="$emit('retry')"
      >
        Retry
      </button>
      <button
        type="button"
        :class="job.reason === 'no-build' ? PRIMARY_BUTTON : SECONDARY_BUTTON"
        @click="$emit('own')"
      >
        Use my own Bun…
      </button>
    </div>
  </section>
</template>
