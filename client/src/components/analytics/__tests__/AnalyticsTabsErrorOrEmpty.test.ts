// A failed analytics load says so (ErrorNote); a load that found nothing says that (EmptyState). They used to share
// one line: "Unable to load analytics (…)." in the empty slot.
import { mount } from "@vue/test-utils";
import { describe, expect, it } from "vitest";
import DailyTab from "@/components/analytics/tabs/DailyTab.vue";
import ProjectsTab from "@/components/analytics/tabs/ProjectsTab.vue";
import SessionsTab from "@/components/analytics/tabs/SessionsTab.vue";
import HorizontalCostBars from "@/components/analytics/charts/HorizontalCostBars.vue";

// One test body for four components with different props.
// eslint-disable-next-line @typescript-eslint/no-explicit-any
const mountAny = (component: unknown, props: Record<string, unknown>) => mount(component as any, { props });

describe("analytics tabs with nothing to show", () => {
  it.each([
    ["DailyTab", DailyTab, { daily: [] }],
    ["ProjectsTab", ProjectsTab, { topProjects: [] }],
    ["HorizontalCostBars", HorizontalCostBars, { items: [] }],
  ] as const)("%s shows the load error, not the empty state, when the load failed", (_name, component, props) => {
    const failed = mountAny(component, { ...props, error: "HTTP 503" });
    expect(failed.get("[data-slot='error-note']").text()).toContain("Unable to load analytics.");
    expect(failed.get("[data-slot='error-note']").text()).toContain("HTTP 503");
    expect(failed.find("[data-slot='empty-state']").exists()).toBe(false);

    const empty = mountAny(component, { ...props, emptyMessage: "Nothing in this range." });
    expect(empty.find("[data-slot='error-note']").exists()).toBe(false);
    expect(empty.get("[data-slot='empty-state']").text()).toContain("Nothing in this range.");
  });

  it("SessionsTab keeps its own words for both", () => {
    const base = { sessions: [], isLoading: false, sortBy: "date", sortDir: "desc" };
    const failed = mountAny(SessionsTab, { ...base, error: "HTTP 500" });
    expect(failed.get("[data-slot='error-note']").text()).toContain("Unable to load session analytics.");
    const empty = mountAny(SessionsTab, base);
    expect(empty.get("[data-slot='empty-state']").text()).toContain("No sessions matched the current analytics filters");
  });
});
