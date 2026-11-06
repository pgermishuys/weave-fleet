import { enableAutoUnmount, flushPromises, mount } from "@vue/test-utils";
import { createPinia, setActivePinia } from "pinia";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

const { getMock, putMock } = vi.hoisted(() => ({ getMock: vi.fn(), putMock: vi.fn() }));

vi.mock("@/api/client", () => ({ api: { GET: getMock, PUT: putMock } }));
vi.mock("@/composables/use-signalr-socket", () => ({ onGlobalEvent: () => () => {} }));
vi.mock("@/composables/use-fleet-updates", () => ({ useFleetUpdatesWatch: () => ({ waiting: { value: 0 } }) }));
vi.mock("@tanstack/vue-router", () => ({ useRouter: () => ({ navigate: vi.fn() }) }));

const { default: builtInSkills } = await import("@/plugins/builtin/built-in-skills");
const { default: mods } = await import("@/plugins/builtin/mods");
const { clearPlugins, configPages, isPluginEnabled, registerPlugin, pluginManifests } = await import("@/plugins/registry");
const { setPluginEnabledEverywhere, usePluginPreferenceSync } = await import("@/plugins/use-plugin-switch");
const { default: PluginsOverview } = await import("@/components/settings/PluginsOverview.vue");
const { useModsRuntimeStore } = await import("@/stores/mods-runtime");
const { MODS_PREFERENCE_KEY } = await import("@/lib/mods");

enableAutoUnmount(afterEach);

const RELEASE = { version: "1.4.2", size: 35_300_000, hasBuild: true, installFolder: "~/.weave/runtimes/bun/1.4.2", source: "github.com/oven-sh/bun" };
const json = (body: unknown) => new Response(JSON.stringify(body), { headers: { "content-type": "application/json" } });

let runtimeView: unknown;

beforeEach(() => {
  localStorage.clear();
  setActivePinia(createPinia());
  getMock.mockReset().mockResolvedValue({ data: {} });
  putMock.mockReset().mockResolvedValue({});
  runtimeView = { bun: null, release: RELEASE, job: null };
  vi.stubGlobal(
    "fetch",
    vi.fn(async (input: RequestInfo | URL) => {
      const path = new URL(String(input), "http://localhost").pathname;
      return path === "/api/features/mods" ? json({ on: false, safeMode: false }) : json(runtimeView);
    }),
  );
  clearPlugins();
  registerPlugin(mods);
  registerPlugin(builtInSkills);
});

afterEach(() => vi.unstubAllGlobals());

describe("Mods and Built-in skills as plugins", () => {
  it("both are in the registry with a settings page, and Built-in skills has no switch of its own", () => {
    expect(pluginManifests.items.value.map((m) => m.descriptor.id)).toEqual(["mods", "built-in-skills"]);
    expect(configPages.get("mods")?.title).toBe("Mods");
    expect(configPages.get("built-in-skills")?.title).toBe("Built-in skills");
    expect(mods.descriptor.trustLevel).toBe("built-in");
    expect(isPluginEnabled("built-in-skills")).toBe(true);
  });

  it("lists both in Settings › Plugins; only Mods has a switch", async () => {
    const wrapper = mount(PluginsOverview);
    await flushPromises();

    expect(wrapper.find("[data-plugin-row='mods']").exists()).toBe(true);
    expect(wrapper.find("[data-plugin-row='built-in-skills']").exists()).toBe(true);
    expect(wrapper.find("[data-testid='plugin-switch-mods']").exists()).toBe(true);
    expect(wrapper.find("[data-testid='plugin-switch-built-in-skills']").exists()).toBe(false);
  });

  it("the Mods switch is the Mods feature preference, not the list of plugins that are off", async () => {
    runtimeView = { bun: { path: "/u/bun", displayPath: "~/bun", source: "installed", version: "1.4.2", safe: true }, release: RELEASE, job: null };
    const wrapper = mount(PluginsOverview);
    await flushPromises();
    const toggle = wrapper.get("[data-testid='plugin-switch-mods']");
    expect(toggle.attributes("aria-checked")).toBe("false");

    await toggle.trigger("click");
    await flushPromises();

    expect(putMock).toHaveBeenCalledTimes(1);
    expect(putMock).toHaveBeenLastCalledWith("/api/preferences/{key}", { params: { path: { key: MODS_PREFERENCE_KEY } }, body: { value: "true" } });
    expect(toggle.attributes("aria-checked")).toBe("true");
  });

  it("an existing Mods preference decides the switch, whatever plugins.disabled says", async () => {
    getMock.mockResolvedValue({ data: { Mods: "true", "plugins.disabled": JSON.stringify(["mods"]) } });
    const wrapper = mount(PluginsOverview);
    await flushPromises();
    usePluginPreferenceSync();

    expect(wrapper.get("[data-testid='plugin-switch-mods']").attributes("aria-checked")).toBe("true");
    expect(isPluginEnabled("mods")).toBe(true);
  });

  it("turning Mods on with no Bun asks first, says what will be installed, and writes nothing yet", async () => {
    const wrapper = mount(PluginsOverview);
    await flushPromises();

    await wrapper.get("[data-testid='plugin-switch-mods']").trigger("click");
    await flushPromises();

    expect(useModsRuntimeStore().confirming).toBe(true);
    const panel = wrapper.get("[data-testid='mods-panel']");
    expect(panel.attributes("data-state")).toBe("confirm");
    expect(panel.text()).toContain("Fleet downloads Bun 1.4.2 (about 35 MB) from github.com/oven-sh/bun");
    expect(panel.text()).toContain("installs it in ~/.weave/runtimes/bun/1.4.2");
    expect(wrapper.get("[data-testid='plugin-switch-mods']").attributes("aria-checked")).toBe("false");
    expect(putMock).not.toHaveBeenCalled();
  });

  it("setPluginEnabledEverywhere for Mods goes through the same switch", async () => {
    runtimeView = { bun: { path: "/u/bun", displayPath: "~/bun", source: "installed", version: "1.4.2", safe: true }, release: RELEASE, job: null };
    await setPluginEnabledEverywhere("mods", true);

    expect(putMock).toHaveBeenCalledWith("/api/preferences/{key}", { params: { path: { key: MODS_PREFERENCE_KEY } }, body: { value: "true" } });
    expect(putMock).not.toHaveBeenCalledWith("/api/preferences/{key}", expect.objectContaining({ params: { path: { key: "plugins.disabled" } } }));
  });
});
