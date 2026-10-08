import { enableAutoUnmount, flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { createPinia, setActivePinia } from "pinia";
import { HOME_MACHINE_KEY, saveMachines, setActiveMachine, type MachineConnection } from "@/lib/machines";
import { useMachinesStore, type MachineCapabilities, type MachineInfo } from "@/stores/machines";
import MachinesSection from "@/components/settings/MachinesSection.vue";

enableAutoUnmount(afterEach);

const atlasInfo: MachineInfo = {
  id: "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
  name: "atlas",
  hostName: "atlas",
  os: "linux",
  version: "0.48.0",
  apiVersion: 1,
  authMode: "token",
  remoteReachable: true,
  requiresToken: false,
};

const mini: MachineConnection = {
  id: "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
  name: "mini",
  baseUrl: "http://100.64.12.40:2113",
  token: "mini-token-0123456789",
  os: "macos",
  addedAt: "2026-10-01T00:00:00.000Z",
};

const miniInfo: MachineInfo = { ...atlasInfo, id: mini.id, name: "mini", hostName: "mini", os: "macos" };

const atlasCapabilities: MachineCapabilities = {
  harnesses: [
    { type: "opencode", name: "OpenCode", available: true, enabled: true, version: "1.18.32" },
    { type: "claude-code", name: "Claude Code", available: true, enabled: true, version: "2.1.0" },
    // Installed but switched off, and switched on but not installed: neither is ready.
    { type: "pi", name: "Pi", available: true, enabled: false, version: "0.9.0" },
    { type: "opencode2", name: "OpenCode 2", available: false, enabled: true, version: null },
  ],
  sessions: { working: 1, needsYou: 2 },
};

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
}

function capabilitiesOf(wrapper: VueWrapper, key: string): string | null {
  const row = wrapper.find(`[data-testid='machine-card'][data-machine='${key}']`);
  const line = row.find("[data-testid='machine-capabilities']");
  return line.exists() ? line.text() : null;
}

describe("Settings → Machines", () => {
  let routes: Record<string, () => Response>;

  beforeEach(() => {
    localStorage.clear();
    sessionStorage.clear();
    setActiveMachine(null);
    setActivePinia(createPinia());
    saveMachines([mini]);
    routes = {
      "/api/machine": () => json({ ...atlasInfo, capabilities: atlasCapabilities }),
      "http://100.64.12.40:2113/api/sessions": () => json([]),
    };
    vi.stubGlobal("fetch", vi.fn(async (input: RequestInfo | URL) => {
      const route = routes[String(input).split("?")[0]];
      return route ? route() : json({ error: "no route" }, 404);
    }));
  });

  afterEach(() => {
    setActiveMachine(null);
    vi.unstubAllGlobals();
  });

  it("shows each machine's ready harnesses and how many sessions work there", async () => {
    routes["http://100.64.12.40:2113/api/machine"] = () => json({
      ...miniInfo,
      capabilities: { harnesses: [], sessions: { working: 0, needsYou: 1 } },
    });

    const wrapper = mount(MachinesSection);
    await flushPromises();

    expect(capabilitiesOf(wrapper, HOME_MACHINE_KEY)).toBe("Ready: OpenCode, Claude Code · 1 working · 2 need you");
    expect(wrapper.find(`[data-machine='${HOME_MACHINE_KEY}'] [data-testid='machine-capabilities']`).attributes("title"))
      .toBe("OpenCode 1.18.32, Claude Code 2.1.0");
    expect(capabilitiesOf(wrapper, mini.id)).toBe("No harness ready · 0 working · 1 needs you");
  });

  it("shows nothing extra for a machine on an older Fleet, or one not answering", async () => {
    routes["http://100.64.12.40:2113/api/machine"] = () => json(miniInfo);

    const wrapper = mount(MachinesSection);
    await flushPromises();
    expect(capabilitiesOf(wrapper, mini.id)).toBeNull();

    // Harnesses not checked yet: only the sessions.
    routes["/api/machine"] = () => json({ ...atlasInfo, capabilities: { harnesses: null, sessions: { working: 3, needsYou: 0 } } });
    routes["http://100.64.12.40:2113/api/machine"] = () => json({ ...miniInfo, capabilities: atlasCapabilities });
    const store = useMachinesStore();
    await store.loadHome();
    await store.refreshMachine(mini.id);
    await flushPromises();
    expect(capabilitiesOf(wrapper, HOME_MACHINE_KEY)).toBe("3 working");
    expect(capabilitiesOf(wrapper, mini.id)).not.toBeNull();

    // Its last numbers would be out of date.
    routes["http://100.64.12.40:2113/api/sessions"] = () => {
      throw new TypeError("Failed to fetch");
    };
    await store.refreshMachine(mini.id);
    await flushPromises();
    expect(capabilitiesOf(wrapper, mini.id)).toBeNull();
  });
});
