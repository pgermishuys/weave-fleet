import { mount } from "@vue/test-utils";
import { createPinia, setActivePinia } from "pinia";
import { defineComponent, h } from "vue";
import { beforeEach, describe, expect, it } from "vitest";
import { useUnsavedFilesGuard } from "@/composables/use-unsaved-files-guard";
import { useFileBuffersStore } from "@/stores/file-buffers";

function unload(): BeforeUnloadEvent {
  const event = new Event("beforeunload", { cancelable: true }) as BeforeUnloadEvent;
  window.dispatchEvent(event);
  return event;
}

describe("useUnsavedFilesGuard", () => {
  beforeEach(() => setActivePinia(createPinia()));

  it("asks before leaving only while a file has unsaved changes", () => {
    const wrapper = mount(defineComponent({ setup: () => (useUnsavedFilesGuard(), () => h("div")) }));
    const buffers = useFileBuffersStore();
    buffers.ensure("s1", "a.ts");

    expect(unload().defaultPrevented).toBe(false);

    buffers.patch("s1", "a.ts", { dirty: true });
    expect(unload().defaultPrevented).toBe(true);

    wrapper.unmount();
  });
});
