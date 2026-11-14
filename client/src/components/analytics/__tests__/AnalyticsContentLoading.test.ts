// Analytics passes each section whether its first answer has come, so none says "No … matched" while it's loading.
import { flushPromises, mount } from "@vue/test-utils";
import { describe, expect, it, vi } from "vitest";
import AnalyticsContent from "@/components/analytics/AnalyticsContent.vue";

const section = vi.hoisted(() => ({ value: "overview" }));
vi.mock("@/composables/use-analytics-section", async () => {
  const { computed } = await import("vue");
  return { useAnalyticsSection: () => computed(() => section.value) };
});
// Every request is still out: each composable is loading, with nothing yet.
const { pending } = vi.hoisted(() => ({
  pending: async () => {
    const { shallowRef } = await import("vue");
    return () => ({
      isLoading: shallowRef(true), error: shallowRef(undefined), refetch: () => new Promise(() => {}),
      summary: shallowRef(null), daily: shallowRef([]), sessions: shallowRef([]), models: shallowRef([]),
    });
  },
}));
vi.mock("@/composables/use-analytics-summary", async () => ({ useAnalyticsSummary: await pending() }));
vi.mock("@/composables/use-analytics-daily", async () => ({ useAnalyticsDaily: await pending() }));
vi.mock("@/composables/use-analytics-sessions", async () => ({ useAnalyticsSessions: await pending() }));
vi.mock("@/composables/use-analytics-models", async () => ({ useAnalyticsModels: await pending() }));
vi.mock("vue-chartjs", () => ({ Line: { render: () => null } }));

describe("Analytics while it loads", () => {
  it.each(["overview", "daily", "projects"])("the %s section says it's loading, not that nothing matched", async (name) => {
    section.value = name;
    const wrapper = mount(AnalyticsContent, { global: { stubs: { AnalyticsSectionStrip: true } } });
    await flushPromises();

    expect(wrapper.text()).not.toContain("matched");
    expect(wrapper.find("[data-slot='empty-state']").exists()).toBe(false);
    expect(wrapper.find("[data-testid='analytics-loading']").exists()).toBe(true);
  });
});
