import { defineComponent, h } from "vue";
import { mount } from "@vue/test-utils";
import { createPinia, setActivePinia } from "pinia";
import { beforeEach, describe, expect, it } from "vitest";
import { useWaitingPage } from "@/composables/use-waiting-page";
import { useCanvasesStore } from "@/stores/canvases";
import { useSessionsStore } from "@/stores/sessions";
import DockedPagePick from "../DockedPagePick.vue";
import PagePickSheet from "../PagePickSheet.vue";

const BottomSheet = defineComponent({ setup: (_, { slots }) => () => h("div", slots.default?.()) });
const PageCanvas = defineComponent({ props: { compact: Boolean }, setup: (props) => () => h("i", { "data-testid": "page", "data-compact": String(props.compact) }) });

function setUp(status: string, choice: "waiting" | "answered" | null) {
  useSessionsStore().setSessions([{ session: { id: "s1" }, activityStatus: status }] as never);
  useCanvasesStore().setServerCanvases("s1", [{
    canvasId: "c1",
    kind: "page",
    title: "Layouts",
    version: 1,
    state: { pageId: "p1", entry: "index.html", choice: choice && { callId: "k1", status: choice, at: "2026-10-16T12:00:00Z" } },
  }]);
  return useWaitingPage("s1");
}

describe("useWaitingPage", () => {
  beforeEach(() => setActivePinia(createPinia()));

  it("finds the page only while the session waits on you and the page waits for a pick", () => {
    expect(setUp("waiting_input", "waiting").value?.page?.title).toBe("Layouts");
    expect(setUp("busy", "waiting").value).toBeNull();
    expect(setUp("waiting_input", "answered").value).toBeNull();
    expect(setUp("waiting_input", null).value).toBeNull();
  });
});

describe("Pick on the page (phone)", () => {
  it("docks the page's title and opens the page", async () => {
    const bar = mount(DockedPagePick, { props: { title: "Settings layouts" } });

    expect(bar.text()).toContain("Pick on the page");
    expect(bar.text()).toContain("Settings layouts");
    await bar.get("[data-testid=docked-page-open]").trigger("click");

    expect(bar.emitted("open")).toHaveLength(1);
  });

  it("shows the page with the same canvas, compact, and goes back from the header", async () => {
    const page = { pageId: "p1", entry: "index.html", source: "", files: 1, bytes: 1, shownAt: "", warnings: [], title: "Settings layouts" };
    const sheet = mount(PagePickSheet, {
      props: { open: true, sessionId: "s1", page },
      global: { stubs: { BottomSheet, PageCanvas } },
    });

    expect(sheet.text()).toContain("Settings layouts");
    expect(sheet.get("[data-testid=page]").attributes("data-compact")).toBe("true");
    await sheet.get("[data-testid=page-pick-back]").trigger("click");

    expect(sheet.emitted("close")).toHaveLength(1);
  });
});
