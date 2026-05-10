<script setup lang="ts">
import type { BoardGroupBy, BoardSortBy, BoardStatus } from "@/stores/board";
import { computed } from "vue";
import { storeToRefs } from "pinia";
import {
  boardSortOptions,
  boardStatusOptions,
  useBoardStore,
} from "@/stores/board";

const groupByOptions: readonly BoardGroupBy[] = ["status", "project", "agent"];

const boardStore = useBoardStore();

const {
  agentFilters,
  availableAgents,
  availableProjects,
  filterSummary,
  groupBy,
  quickStats,
  selectedProject,
  sortBy,
  statusFilters,
} = storeToRefs(boardStore);

const selectedProjectModel = computed({
  get: () => selectedProject.value,
  set: (value: string) => boardStore.setSelectedProject(value),
});

const sortByModel = computed({
  get: () => sortBy.value,
  set: (value: BoardSortBy) => boardStore.setSortBy(value),
});

function handleStatusChange(status: BoardStatus, event: Event): void {
  boardStore.setStatusFilter(status, (event.target as HTMLInputElement).checked);
}

function handleAgentChange(agent: string, event: Event): void {
  boardStore.setAgentFilter(agent, (event.target as HTMLInputElement).checked);
}

function handleGroupByChange(value: BoardGroupBy): void {
  boardStore.setGroupBy(value);
}
</script>

<template>
  <section
    class="board-controls"
    aria-label="Board controls panel"
  >
    <div class="bc-section">
      <p class="bc-label">
        Project
      </p>
      <select
        v-model="selectedProjectModel"
        class="bc-select"
        aria-label="Filter by project"
      >
        <option value="all">
          All projects
        </option>
        <option
          v-for="project in availableProjects"
          :key="project"
          :value="project"
        >
          {{ project }}
        </option>
      </select>
    </div>

    <div class="bc-section">
      <p class="bc-label">
        Status
      </p>
      <label
        v-for="status in boardStatusOptions"
        :key="status.value"
        class="bc-check"
      >
        <input
          :checked="statusFilters[status.value]"
          type="checkbox"
          @change="handleStatusChange(status.value, $event)"
        >
        <span
          class="bc-status-dot"
          :style="{ backgroundColor: status.color }"
          aria-hidden="true"
        />
        <span>{{ status.label }}</span>
      </label>
    </div>

    <div class="bc-section">
      <p class="bc-label">
        Agents
      </p>
      <label
        v-for="agent in availableAgents"
        :key="agent"
        class="bc-check"
      >
        <input
          :checked="agentFilters[agent]"
          type="checkbox"
          @change="handleAgentChange(agent, $event)"
        >
        <span>{{ agent }}</span>
      </label>
    </div>

    <div class="bc-section">
      <p class="bc-label">
        Group by
      </p>
      <div
        class="radio-pills"
        role="radiogroup"
        aria-label="Group board cards by"
      >
        <button
          v-for="option in groupByOptions"
          :key="option"
          type="button"
          class="radio-pill"
          :class="{ active: groupBy === option }"
          :aria-pressed="groupBy === option"
          @click="handleGroupByChange(option)"
        >
          {{ option.charAt(0).toUpperCase() + option.slice(1) }}
        </button>
      </div>
    </div>

    <div class="bc-section">
      <p class="bc-label">
        Sort
      </p>
      <select
        v-model="sortByModel"
        class="bc-select"
        aria-label="Sort board cards"
      >
        <option
          v-for="option in boardSortOptions"
          :key="option.value"
          :value="option.value"
        >
          {{ option.label }}
        </option>
      </select>
    </div>

    <div class="bc-section">
      <p class="bc-label">
        Summary
      </p>
      <p class="bc-summary">
        {{ filterSummary }}
      </p>

      <div
        class="bc-quick-stats"
        aria-label="Board quick stats"
      >
        <div class="bc-stat">
          <span class="bc-stat__value">{{ quickStats.visible }}</span>
          <span class="bc-stat__label">Visible</span>
        </div>

        <div class="bc-stat">
          <span class="bc-stat__value">{{ quickStats.active }}</span>
          <span class="bc-stat__label">Active</span>
        </div>

        <div class="bc-stat">
          <span class="bc-stat__value">{{ quickStats.completed }}</span>
          <span class="bc-stat__label">Done</span>
        </div>

        <div class="bc-stat">
          <span class="bc-stat__value">{{ quickStats.projects }}</span>
          <span class="bc-stat__label">Projects</span>
        </div>
      </div>
    </div>
  </section>
</template>

<style scoped>
.board-controls {
  flex: 1;
  overflow-y: auto;
  padding: 0 12px 12px;
}

.bc-section {
  margin-bottom: 16px;
}

.bc-label {
  margin: 0 0 6px;
  font-size: 10px;
  font-weight: 600;
  text-transform: uppercase;
  letter-spacing: 0.05em;
  color: var(--muted);
}

.bc-select {
  width: 100%;
  background: var(--card-bg);
  border: 1px solid var(--border);
  border-radius: var(--radius-btn);
  padding: 7px 10px;
  color: var(--text);
}

.bc-check {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 3px 0;
  cursor: pointer;
  font-size: 11px;
  color: var(--text);
}

.bc-status-dot {
  width: 8px;
  height: 8px;
  border-radius: 999px;
  flex: 0 0 auto;
}

.radio-pills {
  display: flex;
  gap: 4px;
}

.radio-pill {
  padding: 5px 12px;
  font-size: 10px;
  font-weight: 500;
  border: 1px solid var(--border);
  border-radius: 6px;
  background: transparent;
  color: var(--muted);
  cursor: pointer;
}

.radio-pill.active {
  background: var(--accent-dim);
  border-color: var(--accent);
  color: var(--accent);
}

.bc-summary {
  margin: 0;
  font-size: 11px;
  color: var(--muted);
}

.bc-quick-stats {
  display: flex;
  gap: 12px;
  padding: 10px 0 0;
  border-top: 1px solid var(--border);
  margin-top: 8px;
}

.bc-stat {
  display: flex;
  flex-direction: column;
  gap: 2px;
  min-width: 0;
}

.bc-stat__value {
  font-size: 15px;
  font-weight: 600;
  color: var(--text);
}

.bc-stat__label {
  font-size: 10px;
  color: var(--muted);
}
</style>
