/* eslint-disable vue/one-component-per-file */
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

  // Core no longer names the GitHub rail: a rail exists once a plugin contributes it, so one nobody has
  // registered is an unknown rail and shows the sessions list.
  it("shows the sessions list for a plugin rail nobody has registered", () => {
    openGitHubRail();

    const wrapper = mount(ContextPanel);

    expect(wrapper.find("[data-testid=real-github-panel]").exists()).toBe(false);
    expect(wrapper.text()).toContain("sessions");
  });

  it("shows a placeholder for a plugin rail whose plugin has an icon but no panel", () => {
    runtime.registerPlugin({
      descriptor: githubPlugin.descriptor,
      contributions: { sidebarItems: [{ viewId: "github", label: "GitHub", icon: GitHubStub, defaultPath: "/github" }] },
    });
    openGitHubRail();

    const wrapper = mount(ContextPanel);

    expect(wrapper.find("[data-testid=real-github-panel]").exists()).toBe(false);
    expect(wrapper.text()).toContain("GitHub Panel");
  });

  // A panel that registers after the app mounted still replaces the placeholder (it used to need a reload).
  it("swaps the placeholder for the real panel when the plugin registers after mount", async () => {
    openGitHubRail();
    const wrapper = mount(ContextPanel);
    expect(wrapper.find("[data-testid=real-github-panel]").exists()).toBe(false);

    runtime.registerPlugin(githubPlugin);
    await nextTick();

    expect(wrapper.find("[data-testid=real-github-panel]").exists()).toBe(true);
  });
});
