import { flushPromises, mount } from "@vue/test-utils";
import { createPinia, setActivePinia } from "pinia";
import { beforeEach, describe, expect, it, vi } from "vitest";
import ModPaneCanvas from "@/components/canvas/ModPaneCanvas.vue";
import { liveTarget } from "@/lib/machine-target";
import { modPanes } from "@/lib/mods/points";
import { resetModRenders, watchModSession } from "@/lib/mods/renders";

const { fetchMock, setModOn, setDraftOn } = vi.hoisted(() => ({ fetchMock: vi.fn(), setModOn: vi.fn(), setDraftOn: vi.fn() }));
vi.mock("@/lib/api-client", async (importOriginal) => ({ ...(await importOriginal<object>()), apiFetchOn: fetchMock }));
vi.mock("@/lib/mods/kept-api", async (importOriginal) => ({ ...(await importOriginal<object>()), setModOn, setDraftOn, fetchMods: vi.fn(async () => ({ safeMode: false, mods: [] })) }));

const sites = (panes: unknown[]) => fetchMock.mockImplementation(async () => new Response(JSON.stringify({ sites: [], panes })));
const mountPane = (mod = "ci-watch") => mount(ModPaneCanvas, { props: { sessionId: "s1", paneId: "runs", mod } });
const declared = (extra: object = {}) => ({ mod: "ci-watch", id: "runs", title: "CI runs", icon: null, draft: false, off: true, ...extra });

describe("ModPaneCanvas for a mod that is off", () => {
  beforeEach(() => {
    setActivePinia(createPinia());
    modPanes.clear();
    resetModRenders();
    setModOn.mockReset().mockResolvedValue({ name: "ci-watch", off: null, versions: [] });
    setDraftOn.mockReset().mockResolvedValue({ name: "ci-watch" });
  });

  it("says the mod is off and turns it on with the Settings switch", async () => {
    sites([declared()]);
    watchModSession(liveTarget(), "s1");
    await flushPromises();
    const wrapper = mountPane();
    expect(wrapper.get("[data-testid=mod-pane-off]").text()).toContain("ci-watch is off");
    expect(wrapper.text()).toContain("This pane is drawn by the ci-watch mod. Turn it on to see it here.");
    await wrapper.get("[data-testid=mod-pane-turn-on]").trigger("click");
    expect(setModOn).toHaveBeenCalledWith("ci-watch", true, null);
  });

  it("turns a draft on with the draft's own switch", async () => {
    sites([declared({ draft: true })]);
    watchModSession(liveTarget(), "s1");
    await flushPromises();
    await mountPane().get("[data-testid=mod-pane-turn-on]").trigger("click");
    expect(setDraftOn).toHaveBeenCalledWith("s1", "ci-watch", true, null);
  });

  it("draws the pane once the server says the mod is on", async () => {
    sites([declared()]);
    watchModSession(liveTarget(), "s1");
    await flushPromises();
    modPanes.contribute("t", [{ sessionId: "s1", paneId: "runs", title: "CI runs", mod: "ci-watch", tree: { type: "Text", props: {}, children: ["3 passing"] } as never, mods: [{ name: "ci-watch", draft: false }] }]);
    const wrapper = mountPane();
    expect(wrapper.find("[data-testid=mod-tree]").exists()).toBe(false);
    sites([declared({ off: false })]);
    resetModRenders();
    watchModSession(liveTarget(), "s1");
    await flushPromises();
    expect(wrapper.find("[data-testid=mod-pane-off]").exists()).toBe(false);
    expect(wrapper.get("[data-testid=mod-tree]").text()).toContain("3 passing");
  });

  it("judges a pane the mod never declared by the kept mods", async () => {
    sites([]);
    watchModSession(liveTarget(), "s1");
    await flushPromises();
    expect(mountPane().find("[data-testid=mod-pane-off]").exists()).toBe(false);
  });
});
