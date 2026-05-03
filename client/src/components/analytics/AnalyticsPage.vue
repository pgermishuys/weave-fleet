<script setup lang="ts">
import { computed, shallowRef, watch } from "vue";
import AnalyticsFilters from "@/components/analytics/AnalyticsFilters.vue";
import AnalyticsTabs, { type AnalyticsTabId } from "@/components/analytics/AnalyticsTabs.vue";
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
import { useAnalyticsSummary } from "@/composables/use-analytics-summary";

const activeTab = shallowRef<AnalyticsTabId>("overview");
const sessionsSortBy = shallowRef<AnalyticsSessionsSortBy>("createdAt");
const sessionsSortDir = shallowRef<AnalyticsSessionsSortDir>("desc");
const isRefreshingAnalytics = shallowRef(false);
const isRefreshingSessions = shallowRef(false);

const { filters, resetFilters, setFrom, setProjectId, setTo, topProjects } = useAnalyticsFilters();

const normalizedProjectId = computed(() => filters.value.projectId.trim());
const fromDate = computed(() => filters.value.from || undefined);
const toDate = computed(() => filters.value.to || undefined);
const projectId = computed(() => normalizedProjectId.value || undefined);

const {
  summary,
  error: summaryError,
  refetch: refetchSummary,
} = useAnalyticsSummary({
  from: fromDate,
  to: toDate,
  projectId,
});

const {
  daily,
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

const filterProjects = computed(() => {
  return withSelectedProject(topProjects.value, projectId.value);
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
  return formatFetchMessage(
    dailyError.value,
    projectId.value
      ? "No daily analytics matched the selected date range and project."
      : "No daily analytics matched the selected date range.",
  );
});

const modelsEmptyMessage = computed(() => {
  return formatFetchMessage(
    modelsError.value,
    projectId.value
      ? "No model analytics matched the selected date range and project."
      : "No model analytics matched the selected date range.",
  );
});

const projectsEmptyMessage = computed(() => {
  return formatFetchMessage(
    summaryError.value,
    projectId.value
      ? "No projects matched the selected date range and project."
      : "No project analytics matched the selected date range.",
  );
});

const overviewTabProps = computed(() => ({
  summary: summary.value,
  daily: daily.value,
  models: models.value,
  projects: scopedTopProjects.value,
  dailyEmptyMessage: dailyEmptyMessage.value,
  modelsEmptyMessage: modelsEmptyMessage.value,
  projectsEmptyMessage: projectsEmptyMessage.value,
}));

const projectsTabProps = computed(() => ({
  topProjects: scopedTopProjects.value,
  emptyMessage: projectsEmptyMessage.value,
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
  projects: ProjectsTab,
  sessions: SessionsTab,
  models: ModelsTab,
} as const;

const activeTabComponent = computed(() => tabComponents[activeTab.value]);

const activeTabProps = computed(() => {
  switch (activeTab.value) {
    case "overview":
      return overviewTabProps.value;
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

function handleTabSelect(tabId: AnalyticsTabId): void {
  activeTab.value = tabId;
}

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

function formatFetchMessage(error: string | undefined, fallback: string): string {
  return error ? `Unable to load analytics (${error}).` : fallback;
}
</script>

<template>
  <section
    class="analytics-page"
    aria-label="Analytics dashboard"
  >
    <header class="analytics-page__header">
      <p class="analytics-page__eyebrow">
        Fleet insights
      </p>
      <div class="analytics-page__heading">
        <h1 class="analytics-page__title">
          Analytics
        </h1>
        <p class="analytics-page__subtitle">
          Explore overview metrics, projects, sessions, and models through focused analytics tabs.
        </p>
      </div>
    </header>

    <AnalyticsFilters
      :from="filters.from"
      :to="filters.to"
      :project-id="filters.projectId"
      :projects="filterProjects"
      @update:from="setFrom"
      @update:to="setTo"
      @update:project-id="setProjectId"
      @reset="resetFilters"
    />

    <AnalyticsTabs
      :active-tab="activeTab"
      @select="handleTabSelect"
    />

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
  gap: 24px;
}

.analytics-page__header {
  display: flex;
  flex-direction: column;
  gap: 8px;
}

.analytics-page__eyebrow {
  margin: 0;
  color: var(--muted);
  font-size: 10px;
  font-weight: 600;
  letter-spacing: 0.08em;
  text-transform: uppercase;
}

.analytics-page__heading {
  display: flex;
  flex-direction: column;
  gap: 8px;
}

.analytics-page__title {
  margin: 0;
  color: var(--text);
  font-size: 30px;
  font-weight: 700;
  line-height: 1.1;
}

.analytics-page__subtitle {
  margin: 0;
  max-width: 760px;
  color: var(--muted);
  font-size: 13px;
  line-height: 1.6;
}
</style>
