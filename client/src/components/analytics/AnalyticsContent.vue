<script setup lang="ts">
import { computed, shallowRef, watch } from "vue";
import AnalyticsSectionStrip from "@/components/analytics/AnalyticsSectionStrip.vue";
import DailyTab from "@/components/analytics/tabs/DailyTab.vue";
import ModelsTab from "@/components/analytics/tabs/ModelsTab.vue";
import OverviewTab from "@/components/analytics/tabs/OverviewTab.vue";
import ProjectsTab from "@/components/analytics/tabs/ProjectsTab.vue";
import SessionsTab from "@/components/analytics/tabs/SessionsTab.vue";
import { useAnalyticsDaily } from "@/composables/use-analytics-daily";
import { useAnalyticsFilters, type AnalyticsProjectOption } from "@/composables/use-analytics-filters";
import { useAnalyticsModels } from "@/composables/use-analytics-models";
import {
  useAnalyticsSessions,
  type AnalyticsSessionsSortBy,
  type AnalyticsSessionsSortDir,
} from "@/composables/use-analytics-sessions";
import { useAnalyticsSection } from "@/composables/use-analytics-section";
import { useAnalyticsSummary } from "@/composables/use-analytics-summary";
import { PageHeader } from "@/components/ui/page-header";

// The menu in the context panel picks the section; it is in the address, so a reload keeps it.
const activeTab = useAnalyticsSection();
const sessionsSortBy = shallowRef<AnalyticsSessionsSortBy>("createdAt");
const sessionsSortDir = shallowRef<AnalyticsSessionsSortDir>("desc");
const isRefreshingAnalytics = shallowRef(false);
const isRefreshingSessions = shallowRef(false);

const { filters } = useAnalyticsFilters();

const normalizedProjectId = computed(() => filters.value.projectId.trim());
const fromDate = computed(() => filters.value.from || undefined);
const toDate = computed(() => filters.value.to || undefined);
const projectId = computed(() => normalizedProjectId.value || undefined);

const {
  summary,
  isLoading: isSummaryLoading,
  error: summaryError,
  refetch: refetchSummary,
} = useAnalyticsSummary({
  from: fromDate,
  to: toDate,
  projectId,
});

const {
  daily,
  isLoading: isDailyLoading,
  error: dailyError,
  refetch: refetchDaily,
} = useAnalyticsDaily({
  from: fromDate,
  to: toDate,
  projectId,
});

const {
  sessions,
  isLoading: isSessionsLoading,
  error: sessionsError,
  refetch: refetchSessions,
} = useAnalyticsSessions({
  from: fromDate,
  to: toDate,
  projectId,
  sortBy: sessionsSortBy,
  sortDir: sessionsSortDir,
});

const {
  models,
  isLoading: isModelsLoading,
  error: modelsError,
  refetch: refetchModels,
} = useAnalyticsModels({
  from: fromDate,
  to: toDate,
  projectId,
});

let refreshAnalyticsRequestId = 0;
let refreshSessionsRequestId = 0;

watch([fromDate, toDate, projectId], () => {
  void refreshAnalytics();
}, { immediate: true });

watch([sessionsSortBy, sessionsSortDir], () => {
  void refreshSessionsOnly();
});

const scopedTopProjects = computed<AnalyticsProjectOption[]>(() => {
  const summaryProjects = (summary.value?.topProjects ?? []).map((project) => ({
    id: project.name,
    name: project.name,
    tokens: project.tokens,
    cost: project.cost,
  }));

  return withSelectedProject(summaryProjects, projectId.value);
});

const dailyEmptyMessage = computed(() => {
  return projectId.value
    ? "No daily analytics matched the selected date range and project."
    : "No daily analytics matched the selected date range.";
});

const modelsEmptyMessage = computed(() => {
  return projectId.value
    ? "No model analytics matched the selected date range and project."
    : "No model analytics matched the selected date range.";
});

const projectsEmptyMessage = computed(() => {
  return projectId.value
    ? "No projects matched the selected date range and project."
    : "No project analytics matched the selected date range.";
});

const overviewTabProps = computed(() => ({
  summary: summary.value,
  daily: daily.value,
  models: models.value,
  projects: scopedTopProjects.value,
  dailyEmptyMessage: dailyEmptyMessage.value,
  modelsEmptyMessage: modelsEmptyMessage.value,
  projectsEmptyMessage: projectsEmptyMessage.value,
  dailyError: dailyError.value,
  modelsError: modelsError.value,
  projectsError: summaryError.value,
  dailyLoading: isDailyLoading.value || isRefreshingAnalytics.value,
  // The bars fall back to the summary's top models, so they wait for both.
  modelsLoading: isModelsLoading.value || isSummaryLoading.value || isRefreshingAnalytics.value,
  projectsLoading: isSummaryLoading.value || isRefreshingAnalytics.value,
}));

const dailyTabProps = computed(() => ({
  daily: daily.value,
  emptyMessage: dailyEmptyMessage.value,
  error: dailyError.value,
  isLoading: isDailyLoading.value || isRefreshingAnalytics.value,
}));

const projectsTabProps = computed(() => ({
  topProjects: scopedTopProjects.value,
  emptyMessage: projectsEmptyMessage.value,
  error: summaryError.value,
  isLoading: isSummaryLoading.value || isRefreshingAnalytics.value,
}));

const sessionsTabProps = computed(() => ({
  sessions: sessions.value,
  isLoading: isSessionsLoading.value || isRefreshingAnalytics.value || isRefreshingSessions.value,
  error: sessionsError.value,
  sortBy: sessionsSortBy.value,
  sortDir: sessionsSortDir.value,
}));

const modelsTabProps = computed(() => ({
  models: models.value,
  isLoading: isModelsLoading.value || isRefreshingAnalytics.value,
  error: modelsError.value,
  chartEmptyMessage: modelsEmptyMessage.value,
  tableEmptyMessage: modelsEmptyMessage.value,
}));

const tabComponents = {
  overview: OverviewTab,
  daily: DailyTab,
  projects: ProjectsTab,
  sessions: SessionsTab,
  models: ModelsTab,
} as const;

const activeTabComponent = computed(() => tabComponents[activeTab.value]);

const activeTabProps = computed(() => {
  switch (activeTab.value) {
    case "overview":
      return overviewTabProps.value;
    case "daily":
      return dailyTabProps.value;
    case "projects":
      return projectsTabProps.value;
    case "sessions":
      return sessionsTabProps.value;
    case "models":
      return modelsTabProps.value;
  }

  return overviewTabProps.value;
});

const activeTabListeners = computed(() => {
  if (activeTab.value !== "sessions") {
    return {};
  }

  return {
    "update:sortBy": handleSessionsSortByUpdate,
    "update:sortDir": handleSessionsSortDirUpdate,
  };
});

function handleSessionsSortByUpdate(value: AnalyticsSessionsSortBy): void {
  sessionsSortBy.value = value;
}

function handleSessionsSortDirUpdate(value: AnalyticsSessionsSortDir): void {
  sessionsSortDir.value = value;
}

async function refreshAnalytics(): Promise<void> {
  const currentRequestId = ++refreshAnalyticsRequestId;
  isRefreshingAnalytics.value = true;

  await Promise.allSettled([
    refetchSummary(),
    refetchDaily(),
    refetchSessions(),
    refetchModels(),
  ]);

  if (currentRequestId === refreshAnalyticsRequestId) {
    isRefreshingAnalytics.value = false;
  }
}

async function refreshSessionsOnly(): Promise<void> {
  const currentRequestId = ++refreshSessionsRequestId;
  isRefreshingSessions.value = true;

  await refetchSessions();

  if (currentRequestId === refreshSessionsRequestId) {
    isRefreshingSessions.value = false;
  }
}

function withSelectedProject(
  projects: readonly AnalyticsProjectOption[],
  selectedProjectId: string | undefined,
): AnalyticsProjectOption[] {
  const options = new Map<string, AnalyticsProjectOption>(
    projects.map((project) => [project.id, project]),
  );

  if (selectedProjectId && !options.has(selectedProjectId)) {
    options.set(selectedProjectId, {
      id: selectedProjectId,
      name: selectedProjectId,
      tokens: 0,
      cost: 0,
    });
  }

  return Array.from(options.values());
}

</script>

<template>
  <section
    class="analytics-page"
    aria-label="Analytics dashboard"
  >
    <PageHeader
      title="Analytics"
      description="Tokens and spend across your sessions, by project and model. Costs are estimates at list prices, not a bill."
    />

    <!-- On a phone the menu is a strip of chips here; beside the page it is the context panel. -->
    <AnalyticsSectionStrip class="analytics-page__strip" />

    <component
      :is="activeTabComponent"
      :key="activeTab"
      v-bind="activeTabProps"
      v-on="activeTabListeners"
    />
  </section>
</template>

<style scoped>
.analytics-page {
  display: flex;
  flex-direction: column;
  gap: 20px;
  min-width: 0;
}

.analytics-page__strip {
  display: none;
}

@media (max-width: 767px) {
  .analytics-page__strip {
    display: flex;
  }
}
</style>
