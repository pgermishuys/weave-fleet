import { flushPromises, mount } from "@vue/test-utils";
import { ref } from "vue";
import { beforeEach, describe, expect, it, vi } from "vitest";
import AnalyticsNavPanel from "@/components/analytics/AnalyticsNavPanel.vue";

const { navigate, pathname } = vi.hoisted(() => ({ navigate: vi.fn(), pathname: { value: "/analytics" } }));

vi.mock("@tanstack/vue-router", () => ({
  useRouter: () => ({ navigate }),
  useLocation: () => ref(pathname.value),
}));
vi.mock("@/api/client", () => ({
  api: {
    GET: vi.fn(async (url: string) => {
      if (url.endsWith("/models")) return { data: [{ modelId: "a" }, { modelId: "b" }], error: undefined };
      if (url.endsWith("/sessions")) return { data: [{ sessionId: "1" }, { sessionId: "2" }, { sessionId: "3" }], error: undefined };
      return { data: { totalTokens: 0, totalCost: 0, totalEstimatedCost: 0, sessionCount: 0, messageCount: 0, topModels: [], topProjects: [{ name: "p", tokens: 1, cost: 1 }] }, error: undefined };
    }),
  },
}));

describe("the analytics menu", () => {
  beforeEach(() => {
    localStorage.clear();
    navigate.mockClear();
    pathname.value = "/analytics";
  });

  it("lists Spend (Overview, Daily) and Breakdown (Projects, Models, Sessions) with counts", async () => {
    const wrapper = mount(AnalyticsNavPanel);
    await flushPromises();

    const groups = wrapper.findAll(".analytics-nav__group");
    const labels = groups.map((group) => group.attributes("aria-label"));
    expect(labels).toEqual(["Spend", "Breakdown"]);
    expect(groups[0].findAll("button").map((b) => b.text())).toEqual(["Overview", "Daily"]);
    expect(groups[1].findAll("button").map((b) => b.text())).toEqual(["Projects1", "Models2", "Sessions3"]);
  });

  it("marks the section the address is on", () => {
    pathname.value = "/analytics/models";
    const wrapper = mount(AnalyticsNavPanel);

    expect(wrapper.find("[aria-current=page]").attributes("data-analytics-section")).toBe("models");
  });

  it("opens a section by its own route, and does nothing for the one already open", async () => {
    const wrapper = mount(AnalyticsNavPanel);

    await wrapper.find("[data-analytics-section=sessions]").trigger("click");
    expect(navigate).toHaveBeenLastCalledWith({ to: "/analytics/sessions" });

    navigate.mockClear();
    await wrapper.find("[data-analytics-section=overview]").trigger("click");
    expect(navigate).not.toHaveBeenCalled();
  });

  it("has the From / To / Project filters and Export below the sections, and no By harness", () => {
    const wrapper = mount(AnalyticsNavPanel);

    expect(wrapper.find("[aria-label='Analytics filters']").exists()).toBe(true);
    expect(wrapper.find("a[download]").attributes("href")).toContain("/api/analytics/export?format=csv");
    expect(wrapper.text()).not.toContain("harness");
  });
});
