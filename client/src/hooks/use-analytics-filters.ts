
import { useCallback } from "react";
import { usePersistedState } from "@/hooks/use-persisted-state";

export interface AnalyticsFilters {
  from: string;       // ISO date string
  to: string;         // ISO date string
  projectId: string;  // "" means all projects
}

export interface UseAnalyticsFiltersResult {
  filters: AnalyticsFilters;
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

  const toISO = (d: Date) => d.toISOString().split("T")[0];

  return {
    from: toISO(thirtyDaysAgo),
    to: toISO(today),
    projectId: "",
  };
}

const DEFAULT_FILTERS = getDefaultFilters();

export function useAnalyticsFilters(): UseAnalyticsFiltersResult {
  const [filters, setFilters] = usePersistedState<AnalyticsFilters>(
    STORAGE_KEY,
    DEFAULT_FILTERS
  );

  const setFrom = useCallback(
    (date: string) => setFilters((prev) => ({ ...prev, from: date })),
    [setFilters]
  );

  const setTo = useCallback(
    (date: string) => setFilters((prev) => ({ ...prev, to: date })),
    [setFilters]
  );

  const setProjectId = useCallback(
    (id: string) => setFilters((prev) => ({ ...prev, projectId: id })),
    [setFilters]
  );

  const resetFilters = useCallback(
    () => setFilters(getDefaultFilters()),
    [setFilters]
  );

  return { filters, setFrom, setTo, setProjectId, resetFilters };
}
