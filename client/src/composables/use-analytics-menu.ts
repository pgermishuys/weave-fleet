import { computed, watch } from "vue";
import { useRouter } from "@tanstack/vue-router";
import { useAnalyticsFilters } from "@/composables/use-analytics-filters";
import { useAnalyticsModels } from "@/composables/use-analytics-models";
import { useAnalyticsSection } from "@/composables/use-analytics-section";
import { useAnalyticsSessions } from "@/composables/use-analytics-sessions";
import {
  ANALYTICS_SECTIONS,
  analyticsSectionPath,
  type AnalyticsSection,
  type AnalyticsSectionId,
} from "@/lib/analytics-sections";

export interface AnalyticsMenuItem extends AnalyticsSection {
  /** How many rows the section lists under the current filters; the spend sections have none. */
  count: number | null;
}

/** The analytics-menu: the sections with their counts, the one open, and the filters that scope all of them. */
export function useAnalyticsMenu() {
  const router = useRouter();
  const active = useAnalyticsSection();
  const { filters, topProjects, setFrom, setTo, setProjectId, resetFilters, isDefault } = useAnalyticsFilters();

  const scope = {
    from: computed(() => filters.value.from || undefined),
    to: computed(() => filters.value.to || undefined),
    projectId: computed(() => filters.value.projectId.trim() || undefined),
  };
  const { models, refetch: refetchModels } = useAnalyticsModels(scope);
  const { sessions, refetch: refetchSessions } = useAnalyticsSessions(scope);

  // The composables only poll; the first read, and a read after the filters change, is ours to ask for.
  watch([scope.from, scope.to, scope.projectId], () => {
    void refetchModels();
    void refetchSessions();
  }, { immediate: true });

  const counts = computed<Partial<Record<AnalyticsSectionId, number>>>(() => ({
    projects: topProjects.value.length,
    models: models.value.length,
    sessions: sessions.value.length,
  }));

  const items = computed<readonly AnalyticsMenuItem[]>(() =>
    ANALYTICS_SECTIONS.map((section) => ({ ...section, count: counts.value[section.id] ?? null })),
  );

  const exportHref = computed(() => {
    const query = new URLSearchParams({ format: "csv" });
    if (scope.from.value) query.set("from", scope.from.value);
    if (scope.to.value) query.set("to", scope.to.value);
    if (scope.projectId.value) query.set("projectId", scope.projectId.value);
    return `/api/analytics/export?${query.toString()}`;
  });

  function select(id: AnalyticsSectionId): void {
    if (id === active.value) return;
    void router.navigate({ to: analyticsSectionPath(id) });
  }

  return { items, active, select, filters, projects: topProjects, setFrom, setTo, setProjectId, resetFilters, isDefault, exportHref };
}
