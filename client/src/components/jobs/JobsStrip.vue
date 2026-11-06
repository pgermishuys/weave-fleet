<script setup lang="ts">
import { computed } from "vue";
import { useJobsMenu } from "@/composables/use-jobs-menu";

// The jobs-list on a phone, where the context panel is a drawer: every job as a chip, in the list's order.
const menu = useJobsMenu();
const jobs = computed(() => menu.groups.value.flatMap((group) => group.jobs));
</script>

<template>
  <nav
    class="jobs-strip"
    aria-label="Jobs"
  >
    <button
      v-for="job in jobs"
      :key="job.id"
      type="button"
      class="jobs-strip__chip"
      :class="{ 'jobs-strip__chip--active': menu.active.value === job.id }"
      :aria-current="menu.active.value === job.id ? 'page' : undefined"
      @click="menu.select(job.id)"
    >
      {{ job.title }}
    </button>
  </nav>
</template>

<style scoped>
.jobs-strip {
  display: none;
  gap: 6px;
  overflow-x: auto;
  padding: 8px 20px;
}

.jobs-strip__chip {
  flex-shrink: 0;
  height: 32px;
  padding: 0 12px;
  border: 1px solid var(--border);
  border-radius: 999px;
  background: var(--card-bg);
  color: var(--muted);
  font-size: 12.5px;
  font-weight: 500;
  white-space: nowrap;
}

.jobs-strip__chip--active {
  border-color: color-mix(in srgb, var(--accent) 40%, transparent);
  color: var(--text);
}
</style>

<style scoped>
@media (max-width: 767px) {
  .jobs-strip {
    display: flex;
  }
}
</style>
