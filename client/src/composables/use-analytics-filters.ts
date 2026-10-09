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
  /** True while the filters are the defaults (the last 30 days, every project), so there's nothing to reset. */
  isDefault: ComputedRef<boolean>;
}

const STORAGE_KEY = "weave:analytics:filters";

/** A day as the date pickers send it. The server reads it as a UTC day, so today is the UTC day as well. */
function toIso(value: Date): string {
  return value.toISOString().split("T")[0];
}

function addDays(day: string, days: number): string {
  const date = new Date(`${day}T00:00:00Z`);
  date.setUTCDate(date.getUTCDate() + days);
  return toIso(date);
}

function getDefaultFilters(): AnalyticsFilters {
  const today = toIso(new Date());

  return {
    from: addDays(today, -30),
    to: today,
    projectId: "",
  };
}

/**
 * A range saved on an earlier day, moved to end today and kept the same length, so Analytics doesn't open on the
 * numbers from the day the range was picked. A range with no end already runs to today.
 */
function endingToday(filters: AnalyticsFilters): AnalyticsFilters {
  const today = toIso(new Date());
  if (!filters.to || filters.to === today) return filters;

  const days = Math.round((Date.parse(today) - Date.parse(filters.to)) / 86_400_000);
  return { ...filters, from: filters.from && addDays(filters.from, days), to: today };
}

export function useAnalyticsFilters(): UseAnalyticsFiltersResult {
  const [filters, setFilters] = usePersistedState<AnalyticsFilters>(STORAGE_KEY, getDefaultFilters());
  // Set the state as well as saving it, so the first fetch already asks for the range ending today.
  const opened = endingToday(filters.value);
  if (opened !== filters.value) {
    filters.value = opened;
    setFilters(opened);
  }

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

  const isDefault = computed(() => {
    const defaults = getDefaultFilters();
    return filters.value.from === defaults.from
      && filters.value.to === defaults.to
      && filters.value.projectId === defaults.projectId;
  });

  return {
    filters: computed(() => filters.value),
    topProjects,
    setFrom,
    setTo,
    setProjectId,
    resetFilters,
    isDefault,
  };
}
