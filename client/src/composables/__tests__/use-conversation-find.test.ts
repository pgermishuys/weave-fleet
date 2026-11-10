import { afterEach, describe, expect, it, vi } from "vitest";
import { defineComponent, h, nextTick, ref } from "vue";
import { mount } from "@vue/test-utils";
import { requestConversationFind, useConversationFind } from "@/composables/use-conversation-find";
import type { FindUnit } from "@/lib/conversation-find";

const mounted: { unmount: () => void }[] = [];

function setup(options: { units?: FindUnit[]; hasMore?: boolean; enabled?: boolean } = {}) {
  const units = ref<FindUnit[]>(options.units ?? [
    { id: "m1", turnId: "t1", text: "retry once", toolText: [] },
    { id: "m2", turnId: "t1", text: "no", toolText: ["retry in tool"] },
    { id: "m3", turnId: null, text: "retry twice, retry", toolText: [] },
  ]);
  const hasMore = ref(options.hasMore ?? false);
  const isLoadingOlder = ref(false);
  const loadOlder = vi.fn(() => {
    isLoadingOlder.value = true;
  });
  const reveal = vi.fn();
  const focusComposer = vi.fn();
  const focusField = vi.fn();
  let api!: ReturnType<typeof useConversationFind>;
  const root = document.createElement("div");
  document.body.appendChild(root);
  Object.defineProperty(root, "offsetParent", { get: () => document.body });
  const wrapper = mount(defineComponent({
    setup() {
      api = useConversationFind({
        units,
        hasMore,
        isLoadingOlder,
        loadOlder,
        root: () => root,
        elementOf: () => null,
        reveal,
        scrollTo: () => undefined,
        focusField,
        focusComposer,
        enabled: options.enabled,
      });
      return () => h("div");
    },
  }));
  mounted.push(wrapper);
  return { api, units, hasMore, isLoadingOlder, loadOlder, reveal, focusComposer, focusField, wrapper, root };
}

afterEach(() => {
  mounted.splice(0).forEach((wrapper) => wrapper.unmount());
  document.body.innerHTML = "";
});

describe("useConversationFind", () => {
  it("steps through the matches and wraps both ways", async () => {
    const { api, reveal } = setup();
    api.open();
    api.query.value = "retry";
    await nextTick();
    expect(api.hits.value).toHaveLength(4 - 1);
    api.step(1);
    api.step(1);
    api.step(1);
    expect(api.current.value).toBe(0);
    api.step(-1);
    expect(api.current.value).toBe(2);
    await nextTick();
    expect(reveal).toHaveBeenCalledWith(expect.objectContaining({ unitId: "m3" }));
  });

  it("adds tool output with the scope chip and reveals the folded turn it sits in", async () => {
    const { api, reveal } = setup();
    api.open();
    api.query.value = "retry";
    api.scope.value = "tools";
    await nextTick();
    expect(api.hits.value).toHaveLength(4);
    api.step(1);
    await nextTick();
    expect(reveal).toHaveBeenLastCalledWith(expect.objectContaining({ unitId: "m2", turnId: "t1" }));
  });

  it("loads older pages one after another while open, and stops on Stop or close", async () => {
    const { api, loadOlder, isLoadingOlder, hasMore } = setup({ hasMore: true });
    expect(loadOlder).not.toHaveBeenCalled();
    api.open();
    await nextTick();
    expect(loadOlder).toHaveBeenCalledTimes(1);
    expect(api.searchingOlder.value).toBe(true);
    isLoadingOlder.value = false;
    await nextTick();
    expect(loadOlder).toHaveBeenCalledTimes(2);
    api.stopSearchingOlder();
    isLoadingOlder.value = false;
    await nextTick();
    expect(loadOlder).toHaveBeenCalledTimes(2);
    expect(api.searchingOlder.value).toBe(false);

    api.close();
    api.open();
    isLoadingOlder.value = false;
    await nextTick();
    expect(loadOlder).toHaveBeenCalledTimes(3);
    api.close();
    isLoadingOlder.value = true;
    isLoadingOlder.value = false;
    await nextTick();
    expect(loadOlder).toHaveBeenCalledTimes(3);
    hasMore.value = false;
  });

  it("closes to the composer", () => {
    const { api, focusComposer } = setup();
    api.open();
    api.close();
    expect(api.isOpen.value).toBe(false);
    expect(focusComposer).toHaveBeenCalled();
  });

  describe("Ctrl+F", () => {
    const press = (target: Element | Window) => {
      const event = new KeyboardEvent("keydown", { key: "f", ctrlKey: true, bubbles: true, cancelable: true });
      target.dispatchEvent(event);
      return event;
    };

    it("opens from the conversation or from nothing in particular, and takes the key", async () => {
      const { api, root } = setup();
      const inside = document.createElement("div");
      root.appendChild(inside);
      expect(press(inside).defaultPrevented).toBe(true);
      expect(api.isOpen.value).toBe(true);
      api.close();
      expect(press(document.body).defaultPrevented).toBe(true);
      expect(api.isOpen.value).toBe(true);
    });

    it("leaves the key to a text field, the editor and elements outside the conversation", () => {
      const { api } = setup();
      const field = document.createElement("textarea");
      const editor = document.createElement("div");
      editor.className = "cm-editor";
      const sidebar = document.createElement("button");
      document.body.append(field, editor, sidebar);
      for (const target of [field, editor, sidebar]) {
        expect(press(target).defaultPrevented).toBe(false);
      }
      expect(api.isOpen.value).toBe(false);
    });

    it("stays shut when the conversation isn't the one that takes the key", () => {
      const { api } = setup({ enabled: false });
      expect(press(document.body).defaultPrevented).toBe(false);
      expect(api.isOpen.value).toBe(false);
    });
  });

  it("opens when the menu asks", async () => {
    const { api } = setup();
    requestConversationFind();
    await nextTick();
    expect(api.isOpen.value).toBe(true);
  });
});
