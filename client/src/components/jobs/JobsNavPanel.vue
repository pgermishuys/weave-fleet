<script setup lang="ts">
import { useJobsMenu } from "@/composables/use-jobs-menu";

const menu = useJobsMenu();
</script>

<template>
  <section
    class="jobs-nav"
    aria-label="Jobs navigation"
    data-testid="jobs-nav"
  >
    <p class="jobs-nav__header">
      Jobs
    </p>

    <div
      v-for="group in menu.groups.value"
      :key="group.id"
      class="jobs-nav__group"
      role="group"
      :aria-label="group.label"
      :data-jobs-group="group.id"
    >
      <p class="jobs-nav__group-label">
        {{ group.label }}
        <span class="jobs-nav__count">{{ group.jobs.length }}</span>
      </p>
      <button
        v-for="job in group.jobs"
        :key="job.id"
        type="button"
        class="jobs-nav__item"
        :class="{ 'jobs-nav__item--active': menu.active.value === job.id }"
        :aria-current="menu.active.value === job.id ? 'page' : undefined"
        :data-job-id="job.id"
        :data-state="job.state"
        @click="menu.select(job.id)"
      >
        <span class="jobs-nav__title">{{ job.title }}</span>
        <span
          v-if="job.state === 'needs_you' || job.state === 'failed'"
          class="jobs-nav__flag"
          :class="{ 'jobs-nav__flag--needs': job.state === 'needs_you' }"
        >{{ job.state === "needs_you" ? "Needs you" : "Failed" }}</span>
      </button>
    </div>

    <p
      v-if="menu.isLoaded.value && menu.groups.value.every((group) => group.jobs.length === 0)"
      class="jobs-nav__empty"
    >
      No jobs yet.
    </p>
  </section>
</template>

<style scoped>
.jobs-nav {
  display: flex;
  flex: 1;
  flex-direction: column;
  min-height: 0;
  overflow-y: auto;
  padding: 0 12px 12px;
}

.jobs-nav__header {
  margin: 0;
  padding: 18px 4px 10px;
  font-size: 0.71429rem;
  font-weight: 600;
  text-transform: uppercase;
  letter-spacing: 0.05em;
  color: var(--muted);
}

.jobs-nav__group {
  display: flex;
  flex-direction: column;
  gap: 4px;
  margin-bottom: 8px;
}

.jobs-nav__group-label {
  display: flex;
  justify-content: space-between;
  margin: 8px 0 2px;
  padding: 0 12px;
  font-size: 0.71429rem;
  font-weight: 600;
  text-transform: uppercase;
  letter-spacing: 0.05em;
  color: var(--muted);
  opacity: 0.8;
}

.jobs-nav__count {
  font-variant-numeric: tabular-nums;
}

.jobs-nav__item {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 10px;
  width: 100%;
  min-height: 36px;
  padding: 0 12px;
  border: 1px solid transparent;
  border-radius: var(--radius-btn);
  background: transparent;
  color: var(--muted);
  font-size: 0.85714rem;
  font-weight: 500;
  text-align: left;
  transition: background-color var(--transition), border-color var(--transition), color var(--transition);
}

.jobs-nav__item:hover {
  background: var(--bg);
  color: var(--text);
}

.jobs-nav__item:focus-visible {
  outline: 2px solid var(--accent);
  outline-offset: 2px;
}

.jobs-nav__item--active {
  border-color: color-mix(in srgb, var(--accent) 40%, transparent);
  background: color-mix(in srgb, var(--panel-bg) 88%, var(--accent) 12%);
  color: var(--text);
}

.jobs-nav__title {
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.jobs-nav__flag {
  flex-shrink: 0;
  font-size: 0.71429rem;
  color: var(--muted);
}

.jobs-nav__flag--needs {
  color: var(--accent);
}

.jobs-nav__empty {
  margin: 8px 12px;
  font-size: 0.85714rem;
  color: var(--muted);
}
</style>
