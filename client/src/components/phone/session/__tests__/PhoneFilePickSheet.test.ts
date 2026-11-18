import { mount } from "@vue/test-utils";
import { describe, expect, it, vi } from "vitest";
import { computed, ref } from "vue";
import PhoneFilePickSheet from "@/components/phone/session/PhoneFilePickSheet.vue";

const { failure } = vi.hoisted(() => ({ failure: { value: undefined as string | undefined } }));

vi.mock("@/composables/use-find-files", () => ({
  useFindFiles: () => ({ files: computed(() => []), isLoading: ref(false), error: computed(() => failure.value) }),
}));

const mountSheet = () => mount(PhoneFilePickSheet, {
  props: { open: true, sessionId: "s1" },
  global: { stubs: { BottomSheet: { template: "<div><slot /></div>" } } },
});

describe("PhoneFilePickSheet", () => {
  it("says the search failed, with the reason, instead of showing an empty list", () => {
    failure.value = "HTTP 500";
    const wrapper = mountSheet();
    expect(wrapper.text()).toContain("Couldn't search for files.");
    expect(wrapper.text()).toContain("HTTP 500");
  });

  it("shows no error when the search worked", () => {
    failure.value = undefined;
    expect(mountSheet().text()).not.toContain("Couldn't search");
  });
});
