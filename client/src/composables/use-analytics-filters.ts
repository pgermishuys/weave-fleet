import { computed, watch, type ComputedRef } from "vue";
import { useAnalyticsSummary } from "@/composables/use-analytics-summary";
import { usePersistedState } from "@/composables/use-persisted-state";

export interface AnalyticsFilters {
  from: string;
  to: string;
  projectId: string;
}

export interface AnalyticsProjectOption {
  id: string;
  name: string;
  tokens: number;
  cost: number;
}

export interface UseAnalyticsFiltersResult {
  filters: ComputedRef<AnalyticsFilters>;
  topProjects: ComputedRef<AnalyticsProjectOption[]>;
  setFrom: (date: string) => void;
  setTo: (date: string) => void;
  setProjectId: (id: string) => void;
  resetFilters: () => void;
}

const STORAGE_KEY = "weave:analytics:filters";

function getDefaultFilters(): AnalyticsFilters {
  const today = new Date();
  const thirtyDaysAgo = new Date(today);
  thirtyDaysAgo.setDate(today.getDate() - 30);

  const toIso = (value: Date) => value.toISOString().split("T")[0];

  return {
    from: toIso(thirtyDaysAgo),
    to: toIso(today),
    projectId: "",
  };
}

const DEFAULT_FILTERS = getDefaultFilters();

export function useAnalyticsFilters(): UseAnalyticsFiltersResult {
  const [filters, setFilters] = usePersistedState<AnalyticsFilters>(STORAGE_KEY, DEFAULT_FILTERS);
  const from = computed(() => filters.value.from || undefined);
  const to = computed(() => filters.value.to || undefined);
  const { summary, refetch } = useAnalyticsSummary({ from, to });

  watch([from, to], () => {
    void refetch();
  }, { immediate: true });

  const topProjects = computed<AnalyticsProjectOption[]>(() => {
    return (summary.value?.topProjects ?? []).map((project) => ({
      id: project.name,
      name: project.name,
      tokens: project.tokens,
      cost: project.cost,
    }));
  });

  function setFrom(date: string): void {
    setFilters((previous) => ({ ...previous, from: date }));
  }

  function setTo(date: string): void {
    setFilters((previous) => ({ ...previous, to: date }));
  }

  function setProjectId(id: string): void {
    setFilters((previous) => ({ ...previous, projectId: id }));
  }

  function resetFilters(): void {
    setFilters(getDefaultFilters());
  }

  return {
    filters: computed(() => filters.value),
    topProjects,
    setFrom,
    setTo,
    setProjectId,
    resetFilters,
  };
}
