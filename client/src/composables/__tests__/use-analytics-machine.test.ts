import { mount } from "@vue/test-utils";
import { defineComponent, h } from "vue";
import { describe, expect, it, vi } from "vitest";
import { useAnalyticsSummary } from "@/composables/use-analytics-summary";
import { useAnalyticsFilters } from "@/composables/use-analytics-filters";
import { MACHINE_TARGET, type MachineTarget } from "@/lib/machine-target";

vi.mock("@/api/client", () => ({ api: { GET: vi.fn(async () => ({ data: { from: "live" } })) } }));

function falcon(get: ReturnType<typeof vi.fn>): MachineTarget {
  return { key: "falcon", connection: null, isLive: false, api: { GET: get } as unknown as MachineTarget["api"] };
}

describe("Analytics on another machine", () => {
  it("asks only that machine and keeps its own filters", async () => {
    const get = vi.fn(async () => ({ data: { from: "falcon" } }));
    let summary: ReturnType<typeof useAnalyticsSummary> | undefined;
    let filters: ReturnType<typeof useAnalyticsFilters> | undefined;
    const Probe = defineComponent({
      setup() {
        summary = useAnalyticsSummary();
        filters = useAnalyticsFilters();
        return () => h("div");
      },
    });
    mount(Probe, { global: { provide: { [MACHINE_TARGET as symbol]: () => falcon(get) } } });
    await summary!.refetch();
    const { api } = await import("@/api/client");
    expect(get).toHaveBeenCalledWith("/api/analytics/summary", expect.anything());
    expect(api.GET).not.toHaveBeenCalled();
    filters!.setProjectId("falcon-only");
    expect(localStorage.getItem("weave:analytics:filters:falcon")).toContain("falcon-only");
    expect(localStorage.getItem("weave:analytics:filters")).toBeNull();
  });
});
