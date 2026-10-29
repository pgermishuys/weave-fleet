import { flushPromises, mount } from "@vue/test-utils";
import { createPinia, setActivePinia } from "pinia";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { shallowRef } from "vue";
import HarnessUpdatesGroup from "@/components/settings/HarnessUpdatesGroup.vue";

const { harnesses, refresh, refreshAll, putMock } = vi.hoisted(() => ({
  putMock: vi.fn(),
  harnesses: { value: [] as unknown[] },
  refresh: vi.fn(),
  refreshAll: vi.fn(),
}));

vi.mock("@/api/client", () => ({
  api: {
    GET: vi.fn(() => Promise.resolve({ data: {}, error: undefined })),
    PUT: putMock.mockImplementation(() => Promise.resolve({ data: {}, error: undefined })),
  },
}));
vi.mock("@/composables/use-harnesses", () => ({
  refreshAllHarnesses: refreshAll,
  useHarnesses: () => ({ harnesses: shallowRef(harnesses.value), isLoading: shallowRef(false), refresh }),
}));
vi.mock("@/components/settings/HarnessUpdateStrip.vue", () => ({
  default: { props: ["harness"], template: "<div data-testid='strip-stub'>{{ harness.type }}</div>" },
}));

describe("Updates & about › Harnesses", () => {
  beforeEach(() => {
    setActivePinia(createPinia());
    vi.clearAllMocks();
    harnesses.value = [
      { type: "opencode", displayName: "OpenCode", available: true, userEnabled: true, version: "1.18.20" },
      { type: "pi", displayName: "Pi", available: false, userEnabled: true, version: null },
      { type: "claude-code", displayName: "Claude Code", available: true, userEnabled: false, version: "2.1.0" },
    ];
  });

  it("lists each harness with its version, and the update only for one that is turned on", async () => {
    const wrapper = mount(HarnessUpdatesGroup);
    await flushPromises();

    expect(wrapper.get("[data-setting-row='harness-version-opencode']").text()).toContain("1.18.20");
    expect(wrapper.get("[data-setting-row='harness-version-pi']").text()).toContain("Not installed");
    const stripped = wrapper.findAll("[data-testid='strip-stub']").map((strip) => strip.text());
    expect(stripped).toEqual(["opencode", "pi"]);
  });

  it("keeps the Check for updates switch and Check again", async () => {
    const wrapper = mount(HarnessUpdatesGroup);
    await flushPromises();

    const toggle = wrapper.get("[data-testid='harnesses-update-checks']");
    expect(toggle.attributes("aria-label")).toBe("Check for updates");
    await toggle.trigger("click");
    await flushPromises();
    expect(putMock).toHaveBeenCalledWith("/api/preferences/{key}", { params: { path: { key: "harnessUpdates.check" } }, body: { value: "false" } });

    await wrapper.get("[data-testid='harnesses-check-again']").trigger("click");
    expect(refresh).toHaveBeenCalled();
  });
});
