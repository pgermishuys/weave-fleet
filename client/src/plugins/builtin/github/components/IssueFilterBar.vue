<script setup lang="ts">
import { CircleDot, CircleCheck } from "lucide-vue-next";
import FilterExpressionField from "./FilterExpressionField.vue";
import LabelFilter from "./filters/LabelFilter.vue";
import AuthorFilter from "./filters/AuthorFilter.vue";
import MilestoneFilter from "./filters/MilestoneFilter.vue";
import AssigneeFilter from "./filters/AssigneeFilter.vue";
import SortControl from "./filters/SortControl.vue";
import type { IssueFilterState, GitHubLabel, GitHubMilestone, GitHubAssignee } from "../composables/github-types";

const props = defineProps<{
  filter: IssueFilterState;
  isSearching?: boolean;
  labels: GitHubLabel[];
  labelsLoading: boolean;
  milestones: GitHubMilestone[];
  milestonesLoading: boolean;
  assignees: GitHubAssignee[];
  assigneesLoading: boolean;
}>();

const emit = defineEmits<{
  change: [filter: IssueFilterState];
}>();

function setFilter(partial: Partial<IssueFilterState>) {
  emit("change", { ...props.filter, ...partial });
}

function handleLabelToggle(label: string) {
  const next = props.filter.labels.includes(label)
    ? props.filter.labels.filter((l) => l !== label)
    : [...props.filter.labels, label];
  setFilter({ labels: next });
}

function handleSortChange(sort: "created" | "updated" | "comments", direction: "asc" | "desc") {
  setFilter({ sort, direction });
}
</script>

<template>
  <div class="filter-bar">
    <!-- Expression field — full width -->
    <FilterExpressionField
      :filter="filter"
      :is-searching="isSearching"
      @change="(f) => emit('change', f)"
    />

    <!-- Filter controls row -->
    <div class="filter-controls">
      <!-- State toggle -->
      <button
        :class="['state-btn', filter.state === 'open' && 'state-btn--active']"
        @click="setFilter({ state: 'open' })"
      >
        <CircleDot :size="12" />
        <span>Open</span>
      </button>
      <button
        :class="['state-btn', filter.state === 'closed' && 'state-btn--active']"
        @click="setFilter({ state: 'closed' })"
      >
        <CircleCheck :size="12" />
        <span>Closed</span>
      </button>

      <!-- Separator -->
      <div class="filter-separator" />

      <!-- Filter dropdowns -->
      <LabelFilter
        :labels="labels"
        :is-loading="labelsLoading"
        :selected="filter.labels"
        @toggle="handleLabelToggle"
      />
      <AuthorFilter
        :users="assignees"
        :is-loading="assigneesLoading"
        :selected="filter.author"
        @select="(v) => setFilter({ author: v })"
      />
      <MilestoneFilter
        :milestones="milestones"
        :is-loading="milestonesLoading"
        :selected="filter.milestone"
        @select="(v) => setFilter({ milestone: v })"
      />
      <AssigneeFilter
        :assignees="assignees"
        :is-loading="assigneesLoading"
        :selected="filter.assignee"
        @select="(v) => setFilter({ assignee: v })"
      />

      <!-- Sort — pushed to right -->
      <div class="filter-sort">
        <SortControl
          :sort="filter.sort"
          :direction="filter.direction"
          @change="handleSortChange"
        />
      </div>
    </div>
  </div>
</template>

<style scoped>
.filter-bar {
  display: flex;
  flex-direction: column;
  gap: 6px;
  padding: 8px 12px;
  border-bottom: 1px solid var(--border);
}

.filter-controls {
  display: flex;
  align-items: center;
  gap: 2px;
  flex-wrap: wrap;
}

.state-btn {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  padding: 2px 8px;
  height: 28px;
  border-radius: 6px;
  border: none;
  background: transparent;
  color: var(--muted);
  font-size: 11px;
  cursor: pointer;
}

.state-btn:hover {
  background: var(--sidebar-item-hover);
  color: var(--text);
}

.state-btn--active {
  background: var(--accent-muted, rgba(99, 102, 241, 0.15));
  color: var(--accent);
}

.filter-separator {
  width: 1px;
  height: 16px;
  background: var(--border);
  margin: 0 4px;
}

.filter-sort {
  margin-left: auto;
}
</style>
