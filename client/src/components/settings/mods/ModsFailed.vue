<script setup lang="ts">
import { computed } from "vue";
import type { ModsRuntimeJob, ModsRuntimeRelease } from "@/lib/mods-runtime";
import { PRIMARY_BUTTON } from "./classes";

/**
 * The install failed and the switch went back off (step 5): the reason in plain words and Retry, which is hidden
 * when there is no build for this computer, since trying again can't help.
 */
const props = defineProps<{ job: ModsRuntimeJob; release: ModsRuntimeRelease }>();

defineEmits<{ retry: [] }>();

const hint = computed(() => {
  if (props.job.reason === "offline") return "If GitHub is blocked here, install Bun yourself and point Fleet at it.";
  if (props.job.reason === "blocked") return `Ask whoever runs your network to allow ${props.release.source}, or install Bun yourself and point Fleet at it.`;
  return "";
});
</script>

<template>
  <section
    aria-labelledby="mods-failed-heading"
    class="space-y-3"
  >
    <h4
      id="mods-failed-heading"
      class="text-sm font-medium text-error"
    >
      <span aria-hidden="true">⚠</span> Couldn't download Bun
    </h4>
    <p class="max-w-xl text-sm leading-6 text-muted">
      {{ job.message }} {{ hint }}
    </p>
    <div
      v-if="job.reason !== 'no-build'"
      class="flex flex-wrap items-center gap-2"
    >
      <button
        type="button"
        :class="PRIMARY_BUTTON"
        @click="$emit('retry')"
      >
        Retry
      </button>
    </div>
  </section>
</template>
