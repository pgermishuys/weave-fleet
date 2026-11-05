import { describe, expect, it, vi } from "vitest";
import { computed } from "vue";
import { mount } from "@vue/test-utils";
import NoHarnessNotice from "../NoHarnessNotice.vue";

const reason = vi.hoisted(() => ({ value: null as string | null }));
vi.mock("@/composables/use-enabled-harnesses", () => ({
  useEnabledHarnesses: () => ({ noHarnessReason: computed(() => reason.value) }),
}));

describe("NoHarnessNotice", () => {
  it("says no harness is ready when none can start a session", () => {
    reason.value = "OpenCode isn't installed";
    const wrapper = mount(NoHarnessNotice);
    expect(wrapper.get("[data-testid=phone-no-harness]").text()).toContain("No harness is ready yet");
  });

  it("shows nothing while a harness is ready", () => {
    reason.value = null;
    expect(mount(NoHarnessNotice).find("[data-testid=phone-no-harness]").exists()).toBe(false);
  });
});
