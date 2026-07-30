<script setup lang="ts">
import { CircleDot, CircleCheck } from "lucide-vue-next";
import FilterExpressionField from "./FilterExpressionField.vue";
import LabelFilter from "./filters/LabelFilter.vue";
import AuthorFilter from "./filters/AuthorFilter.vue";
import MilestoneFilter from "./filters/MilestoneFilter.vue";
import AssigneeFilter from "./filters/AssigneeFilter.vue";
import SortControl from "./filters/SortControl.vue";
import { Button } from "@/components/ui/button";
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
      <Button
        variant="filter"
        size="sm"
        :data-active="filter.state === 'open'"
        @click="setFilter({ state: 'open' })"
      >
        <CircleDot :size="12" />
        <span>Open</span>
      </Button>
      <Button
        variant="filter"
        size="sm"
        :data-active="filter.state === 'closed'"
        @click="setFilter({ state: 'closed' })"
      >
        <CircleCheck :size="12" />
        <span>Closed</span>
      </Button>

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
