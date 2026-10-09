import { mount } from "@vue/test-utils";
import { defineComponent, h, nextTick } from "vue";
import { beforeEach, describe, expect, it, vi } from "vitest";
import ContextPanel from "@/components/layout/ContextPanel.vue";
import { usePluginRuntime } from "@/plugins/composable";
import { useSidebarStore } from "@/stores/sidebar";

// The other panels reach for the server; this test only cares which panel shows.
vi.mock("@/components/sessions/SessionsPanel.vue", () => ({ default: defineComponent({ render: () => h("div", "sessions") }) }));
vi.mock("@/components/board/BoardControlsPanel.vue", () => ({ default: defineComponent({ render: () => h("div", "board") }) }));
vi.mock("@/components/settings/SettingsNavPanel.vue", () => ({ default: defineComponent({ render: () => h("div", "settings") }) }));
vi.mock("@/components/automations/AutomationsNavPanel.vue", () => ({ default: defineComponent({ render: () => h("div", "automations") }) }));
vi.mock("@/components/workflows/WorkflowsNavPanel.vue", () => ({ default: defineComponent({ render: () => h("div", "workflows") }) }));

const GitHubStub = defineComponent({ render: () => h("div", { "data-testid": "real-github-panel" }, "github panel") });

const githubPlugin = {
  descriptor: { id: "github", displayName: "GitHub", trustLevel: "built-in", hasFrontend: true, hasBackend: false },
  contributions: { sidebarPanels: [{ viewId: "github", component: GitHubStub }] },
} as const;

function openGitHubRail(): void {
  useSidebarStore().setActiveRail("github");
}

describe("ContextPanel plugin panels", () => {
  const runtime = usePluginRuntime();

  beforeEach(() => {
    globalThis.localStorage?.clear();
    runtime.clear();
  });

  it("shows the plugin's panel for its rail when the plugin registered before mount", () => {
    runtime.registerPlugin(githubPlugin);
    openGitHubRail();

    const wrapper = mount(ContextPanel);

    expect(wrapper.find("[data-testid=real-github-panel]").exists()).toBe(true);
  });

  it("shows a placeholder for a plugin rail nobody has registered", () => {
    openGitHubRail();

    const wrapper = mount(ContextPanel);

    expect(wrapper.find("[data-testid=real-github-panel]").exists()).toBe(false);
    expect(wrapper.text()).toContain("GitHub Panel");
  });

  // Known bug: ContextPanel reads the plugin Map directly, so a panel registered after mount never appears.
  it.fails("swaps the placeholder for the real panel when the plugin registers after mount", async () => {
    openGitHubRail();
    const wrapper = mount(ContextPanel);
    expect(wrapper.find("[data-testid=real-github-panel]").exists()).toBe(false);

    runtime.registerPlugin(githubPlugin);
    await nextTick();

    expect(wrapper.find("[data-testid=real-github-panel]").exists()).toBe(true);
  });
});
