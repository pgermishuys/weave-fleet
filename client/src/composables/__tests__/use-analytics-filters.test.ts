import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { shallowRef } from "vue";
import { mountComposable } from "@/composables/__tests__/test-utils";
import { useAnalyticsFilters } from "@/composables/use-analytics-filters";

vi.mock("@/composables/use-analytics-summary", () => ({
  useAnalyticsSummary: () => ({ summary: shallowRef(null), refetch: vi.fn() }),
}));

const STORAGE_KEY = "weave:analytics:filters";

function save(filters: { from: string; to: string; projectId: string }): void {
  localStorage.setItem(STORAGE_KEY, JSON.stringify(filters));
}

async function open() {
  const { result } = await mountComposable(() => useAnalyticsFilters());
  return result;
}

beforeEach(() => {
  localStorage.clear();
  vi.useFakeTimers({ toFake: ["Date"] });
  vi.setSystemTime(new Date("2026-10-09T12:00:00Z"));
});

afterEach(() => {
  vi.useRealTimers();
});

describe("useAnalyticsFilters", () => {
  it("opens on the last 30 days, ending today", async () => {
    const analytics = await open();

    expect(analytics.filters.value).toEqual({ from: "2026-09-09", to: "2026-10-09", projectId: "" });
    expect(analytics.isDefault.value).toBe(true);
  });

  it("opens ending today when Fleet has been open since an earlier day", async () => {
    vi.setSystemTime(new Date("2026-10-12T12:00:00Z"));

    const analytics = await open();

    expect(analytics.filters.value).toEqual({ from: "2026-09-12", to: "2026-10-12", projectId: "" });
  });

  it("moves a range saved on an earlier day to end today, keeping its length and project", async () => {
    save({ from: "2026-09-03", to: "2026-10-06", projectId: "harbor-api" });

    const analytics = await open();

    expect(analytics.filters.value).toEqual({ from: "2026-09-06", to: "2026-10-09", projectId: "harbor-api" });
  });

  it("keeps the default range the default on a later day", async () => {
    save({ from: "2026-09-05", to: "2026-10-05", projectId: "" });

    const analytics = await open();

    expect(analytics.filters.value).toEqual({ from: "2026-09-09", to: "2026-10-09", projectId: "" });
    expect(analytics.isDefault.value).toBe(true);
  });

  it("leaves a range alone while Analytics stays open", async () => {
    const analytics = await open();

    analytics.setTo("2026-09-30");

    expect(analytics.filters.value.to).toBe("2026-09-30");
  });

  it("leaves a range with no end alone", async () => {
    save({ from: "2026-09-03", to: "", projectId: "" });

    const analytics = await open();

    expect(analytics.filters.value).toEqual({ from: "2026-09-03", to: "", projectId: "" });
  });
});
