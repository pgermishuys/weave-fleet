import { flushPromises, mount } from "@vue/test-utils";
import { ref } from "vue";
import { beforeEach, describe, expect, it, vi } from "vitest";
import ContextPanel from "@/components/layout/ContextPanel.vue";
import IconRail from "@/components/layout/IconRail.vue";
import { usePluginRuntime } from "@/plugins/composable";
import { useSidebarStore } from "@/stores/sidebar";
import githubPluginManifest from "@/plugins/builtin/github";
import marketplacePluginManifest from "@/plugins/builtin/marketplace";

const { navigate, pathname, boardOn, workflowsOn, missionOn } = vi.hoisted(() => ({
  navigate: vi.fn(),
  pathname: { value: "/" },
  boardOn: { value: false },
  workflowsOn: { value: false },
  missionOn: { value: false },
}));

vi.mock("@tanstack/vue-router", () => ({
  useRouter: () => ({ navigate }),
  useLocation: () => ref(pathname.value),
}));
vi.mock("@/api/client", () => ({
  api: { GET: vi.fn().mockResolvedValue({ data: { statuses: [] }, error: undefined, response: { status: 200 } }) },
}));
vi.mock("@/composables/use-board-feature", () => ({ useBoardFeature: () => ({ isBoardFeatureEnabled: ref(boardOn.value) }) }));
vi.mock("@/composables/use-mission-control-feature", () => ({ useMissionControlFeature: () => ({ isMissionControlEnabled: ref(missionOn.value) }) }));
vi.mock("@/composables/use-workflows-feature", () => ({ useWorkflowsFeature: () => ({ isWorkflowsEnabled: ref(workflowsOn.value) }) }));
vi.mock("@/stores/workflows", () => ({ useWorkflowsStore: () => ({ ensureLoaded: vi.fn() }) }));
vi.mock("@/composables/use-whats-new", () => ({ useWhatsNew: () => ({ openWhatsNew: vi.fn() }) }));
vi.mock("@/composables/use-media-query", () => ({ useIsMobileNav: () => ref(false) }));

const panel = vi.hoisted(() => (name: string) => async () => {
  const { defineComponent, h } = await import("vue");
  return { default: defineComponent({ render: () => h("div", { "data-testid": `panel-${name}` }, name) }) };
});
vi.mock("@/components/sessions/SessionsPanel.vue", panel("sessions"));
vi.mock("@/components/board/BoardControlsPanel.vue", panel("board"));
vi.mock("@/components/settings/SettingsNavPanel.vue", panel("settings"));
vi.mock("@/components/automations/AutomationsNavPanel.vue", panel("automations"));
vi.mock("@/components/workflows/WorkflowsNavPanel.vue", panel("workflows"));
vi.mock("@/plugins/builtin/github/GitHubPanel.vue", panel("github"));
vi.mock("@/plugins/builtin/github/GitHubSettings.vue", panel("github-settings"));
vi.mock("@/plugins/builtin/marketplace/MarketplacePanel.vue", panel("marketplace"));

function railLabels(wrapper: { findAll: (selector: string) => { attributes: (name: string) => string | undefined }[] }): string[] {
  return wrapper.findAll("nav.rail-nav button.rail-item").map((button) => button.attributes("aria-label") ?? "");
}

describe("the icon rail", () => {
  const runtime = usePluginRuntime();

  beforeEach(() => {
    localStorage.clear();
    runtime.clear();
    runtime.registerPlugins([githubPluginManifest, marketplacePluginManifest]);
    pathname.value = "/";
    boardOn.value = false;
    workflowsOn.value = false;
    missionOn.value = false;
    navigate.mockClear();
  });

  it("lists every rail with its name under the icon, Workflows included while it's off", () => {
    const wrapper = mount(IconRail);

    expect(railLabels(wrapper)).toEqual([
      "Sessions", "Workflows", "Automations", "Analytics", "GitHub", "Plugins", "Settings", "Help",
    ]);
    expect(wrapper.findAll("nav.rail-nav .rail-label").map((label) => label.text())).toEqual(railLabels(wrapper));
    expect(wrapper.find("[data-tooltip]").exists()).toBe(false);
  });

  it("puts Fleet first once Mission control is on, and opens it", async () => {
    missionOn.value = true;

    const wrapper = mount(IconRail);

    expect(railLabels(wrapper).slice(0, 3)).toEqual(["Fleet", "Sessions", "Workflows"]);
    await wrapper.find("[aria-label=Fleet]").trigger("click");
    expect(navigate).toHaveBeenLastCalledWith({ to: "/fleet" });
  });

  it("marks only the switched-off rails", () => {
    const wrapper = mount(IconRail);

    expect(wrapper.findAll("[data-off=true]").map((button) => button.attributes("aria-label"))).toEqual(["Workflows"]);
    expect(wrapper.find("[aria-label=Workflows] .rail-off-dot").exists()).toBe(true);
    expect(wrapper.find("[aria-label=Sessions] .rail-off-dot").exists()).toBe(false);
  });

  it("adds Board after Sessions, unmarked, once it's on", () => {
    boardOn.value = true;

    const wrapper = mount(IconRail);

    expect(railLabels(wrapper).slice(0, 3)).toEqual(["Sessions", "Board", "Workflows"]);
    expect(wrapper.find("[aria-label=Board]").attributes("data-off")).toBeUndefined();
  });

  it("drops the marker from Workflows once it's on", () => {
    boardOn.value = true;
    workflowsOn.value = true;

    const wrapper = mount(IconRail);

    expect(wrapper.find("[data-off=true]").exists()).toBe(false);
    expect(wrapper.find(".rail-off-dot").exists()).toBe(false);
  });

  it("still opens a switched-off rail's page", async () => {
    const wrapper = mount(IconRail);

    await wrapper.find("[aria-label=Workflows]").trigger("click");
    expect(navigate).toHaveBeenLastCalledWith({ to: "/workflows" });
  });

  it("makes a clicked rail active and navigates when it has a path", async () => {
    const wrapper = mount(IconRail);
    const store = useSidebarStore();

    await wrapper.find("[aria-label=Automations]").trigger("click");
    expect(store.activeRail).toBe("automations");
    expect(navigate).toHaveBeenLastCalledWith({ to: "/automations" });

    navigate.mockClear();
    await wrapper.find("[aria-label=Plugins]").trigger("click");
    expect(store.activeRail).toBe("marketplace");
    expect(navigate).not.toHaveBeenCalled();

    await wrapper.find("[aria-label=GitHub]").trigger("click");
    expect(store.activeRail).toBe("github");
    expect(navigate).toHaveBeenLastCalledWith({ to: "/github" });
  });

  it("follows the route to a rail, and leaves the rail alone on a route that has none", () => {
    const store = useSidebarStore();
    pathname.value = "/analytics";
    mount(IconRail);
    expect(store.activeRail).toBe("analytics");

    pathname.value = "/somewhere-else";
    mount(IconRail);
    expect(store.activeRail).toBe("analytics");
  });

  it("highlights Fleet, not Sessions, on mission control", () => {
    missionOn.value = true;
    pathname.value = "/fleet";

    const wrapper = mount(IconRail);

    expect(useSidebarStore().activeRail).toBe("fleet");
    expect(wrapper.find("[aria-label=Fleet]").classes()).toContain("active");
    expect(wrapper.find("[aria-label=Sessions]").classes()).not.toContain("active");
  });

  it("follows a plugin's path to the plugin's rail", () => {
    pathname.value = "/github/pulls";
    mount(IconRail);

    expect(useSidebarStore().activeRail).toBe("github");
  });

  it("marks the active rail", async () => {
    const wrapper = mount(IconRail);
    useSidebarStore().setActiveRail("settings");
    await flushPromises();

    expect(wrapper.find("[aria-label=Settings]").classes()).toContain("active");
    expect(wrapper.find("[aria-label=Sessions]").classes()).not.toContain("active");
  });
});

describe("the context panel per rail", () => {
  const runtime = usePluginRuntime();

  beforeEach(() => {
    localStorage.clear();
    runtime.clear();
    runtime.registerPlugins([githubPluginManifest, marketplacePluginManifest]);
    boardOn.value = false;
    workflowsOn.value = false;
  });

  function shown(rail: string): string {
    useSidebarStore().setActiveRail(rail as never);
    const wrapper = mount(ContextPanel);
    const found = wrapper.find("[data-testid^=panel-]");
    return found.exists() ? (found.attributes("data-testid") ?? "") : wrapper.text();
  }

  it.each([
    ["sessions", "panel-sessions"],
    ["analytics", "panel-sessions"],
    ["automations", "panel-automations"],
    ["settings", "panel-settings"],
    ["github", "panel-github"],
    ["marketplace", "panel-marketplace"],
  ])("shows %s as %s", (rail, expected) => {
    expect(shown(rail)).toBe(expected);
  });

  it("shows the workflows library only while Workflows is on", () => {
    expect(shown("workflows")).toBe("panel-sessions");
    workflowsOn.value = true;
    expect(shown("workflows")).toBe("panel-workflows");
  });

  it("shows the sessions panel for Board while Board is off", () => {
    expect(shown("board")).toBe("panel-sessions");
  });

  it("shows the board controls for Board while Board is on", () => {
    boardOn.value = true;
    expect(shown("board")).toBe("panel-board");
  });

  it("falls back to the sessions panel for a rail nobody knows", () => {
    expect(shown("no-such-rail")).toBe("panel-sessions");
  });

  it("keeps an unknown rail id in the store without persisting it", () => {
    const store = useSidebarStore();
    store.setActiveRail("no-such-rail" as never);

    expect(store.activeRail).toBe("no-such-rail");
    expect(Object.keys(localStorage).some((key) => key.includes("rail"))).toBe(false);
  });
});
