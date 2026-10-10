<script setup lang="ts">
import { computed } from "vue";
import { TriangleAlert } from "lucide-vue-next";
import { failureCause, type ModsRuntimeBun, type ModsRuntimeJob } from "@/lib/mods-runtime";
import { PRIMARY_BUTTON } from "./classes";

/** A security update couldn't be installed (step 9b). Amber, not red: mods keep running and Fleet tries again. */
const props = defineProps<{
  bun: ModsRuntimeBun;
  job: ModsRuntimeJob;
  busy?: boolean;
}>();

defineEmits<{ retry: [] }>();

const cause = computed(() => failureCause(props.job));
</script>

<template>
  <section
    aria-labelledby="mods-security-failed-heading"
    class="space-y-3"
  >
    <h4
      id="mods-security-failed-heading"
      class="flex items-center gap-2 text-sm font-medium text-idle"
    >
      <TriangleAlert
        :size="16"
        aria-hidden="true"
      />
      Bun {{ bun.version }} needs a security fix
    </h4>
    <div class="max-w-xl space-y-1 text-sm leading-6 text-muted">
      <p>Bun {{ job.version }} fixes a security problem in {{ bun.version }}. {{ cause }}</p>
      <p>Mods keep running on {{ bun.version }}, and Fleet tries again every 4 hours.</p>
    </div>
    <div class="flex flex-wrap items-center gap-2">
      <button
        type="button"
        :class="PRIMARY_BUTTON"
        :disabled="busy"
        @click="$emit('retry')"
      >
        Retry
      </button>
    </div>
  </section>
</template>
