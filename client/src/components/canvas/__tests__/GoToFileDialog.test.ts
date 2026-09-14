import { flushPromises, mount } from "@vue/test-utils";
import { defineComponent, h, ref } from "vue";
import { beforeEach, describe, expect, it, vi } from "vitest";
import GoToFileDialog from "@/components/canvas/GoToFileDialog.vue";
import { useCanvasesStore } from "@/stores/canvases";
import { useGoToFileStore } from "@/stores/go-to-file";

const { files, queries, showRightPanel } = vi.hoisted(() => ({
  files: { value: [] as string[] },
  queries: [] as (string | null)[],
  showRightPanel: vi.fn(),
}));

vi.mock("@/composables/use-find-files", async () => {
  const { computed, ref: vueRef, toValue } = await import("vue");
  return {
    useFindFiles: (_session: unknown, query: () => string | null) => {
      const result = vueRef<string[]>([]);
      return {
        files: computed(() => {
          const q = toValue(query);
          queries.push(q);
          return q ? files.value.filter((path) => path.toLowerCase().includes(q.toLowerCase())) : result.value;
        }),
        isLoading: vueRef(false),
        error: vueRef(undefined),
      };
    },
  };
});

vi.mock("@/composables/use-sidebar-mobile", () => ({ useSidebarMobile: () => ({ showRightPanel }) }));

// reka-ui renders dialog content through a portal; show it in place.
vi.mock("@/components/ui/dialog", () => {
  const pass = (name: string) => defineComponent({ name, props: { open: Boolean }, setup: (_p, { slots }) => () => h("div", slots.default?.()) });
  return {
    Dialog: defineComponent({ name: "Dialog", props: { open: Boolean }, setup: (props, { slots }) => () => (props.open ? h("div", slots.default?.()) : null) }),
    DialogContent: pass("DialogContent"),
    DialogTitle: pass("DialogTitle"),
    DialogDescription: pass("DialogDescription"),
  };
});

describe("GoToFileDialog", () => {
  beforeEach(() => {
    files.value = ["src/middleware/auth.ts", "src/utils/token.ts", "docs/auth.md", "src/components/"];
    showRightPanel.mockReset();
  });

  async function opened() {
    const wrapper = mount(GoToFileDialog, { attachTo: document.body });
    useGoToFileStore().show("s1");
    await flushPromises();
    return wrapper;
  }

  it("lists the files already open when nothing is typed", async () => {
    useCanvasesStore().openFile("s1", "src/utils/token.ts", { keep: true });
    const wrapper = await opened();
    expect(wrapper.text()).toContain("Open");
    expect(wrapper.find('[data-testid="go-to-file-item-src/utils/token.ts"]').exists()).toBe(true);
    wrapper.unmount();
  });

  it("filters to files as you type, leaving out folders", async () => {
    const wrapper = await opened();
    await wrapper.get('[data-testid="go-to-file-input"]').setValue("auth");
    await flushPromises();

    const items = wrapper.findAll('[role="option"]').map((item) => item.attributes("data-testid"));
    expect(items).toEqual(["go-to-file-item-src/middleware/auth.ts", "go-to-file-item-docs/auth.md"]);
    wrapper.unmount();
  });

  it("Enter opens the chosen file as a kept tab and closes", async () => {
    const wrapper = await opened();
    const input = wrapper.get('[data-testid="go-to-file-input"]');
    await input.setValue("auth");
    await flushPromises();
    await input.trigger("keydown", { key: "ArrowDown" });
    await input.trigger("keydown", { key: "Enter" });

    const state = useCanvasesStore().sessionCanvases("s1");
    expect(state.activeId).toBe("file:docs/auth.md");
    expect(state.canvases.find((canvas) => canvas.id === "file:docs/auth.md")?.file?.preview).toBe(false);
    expect(useGoToFileStore().sessionId).toBeNull();
    expect(useGoToFileStore().focusOnOpen).toEqual({ sessionId: "s1", path: "docs/auth.md" });
    expect(showRightPanel).toHaveBeenCalled();
    wrapper.unmount();
  });

  it("says so when nothing matches", async () => {
    const wrapper = await opened();
    await wrapper.get('[data-testid="go-to-file-input"]').setValue("zzz");
    await flushPromises();
    expect(wrapper.text()).toContain("No files match.");
    wrapper.unmount();
  });
});
