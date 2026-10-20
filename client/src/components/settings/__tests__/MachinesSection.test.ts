import { enableAutoUnmount, flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { createPinia, setActivePinia } from "pinia";
import { HOME_MACHINE_KEY, saveMachines, setActiveMachine, type MachineConnection } from "@/lib/machines";
import { useMachinesStore, type MachineCapabilities, type MachineInfo } from "@/stores/machines";
import MachinesSection from "@/components/settings/MachinesSection.vue";

vi.mock("@tanstack/vue-router", () => ({ useRouter: () => ({ navigate: vi.fn() }) }));

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

  it("says '1 session' for a reachable machine with one session", async () => {
    routes["http://100.64.12.40:2113/api/machine"] = () => json(miniInfo);
    routes["http://100.64.12.40:2113/api/sessions"] = () => json([{ session: { id: "s1", title: "One", time: { created: 1, updated: 1 } }, instanceId: "i1", sessionStatus: "idle", activityStatus: "idle", retentionStatus: "active" }]);

    const wrapper = mount(MachinesSection);
    await flushPromises();
    const state = wrapper.find(`[data-testid='machine-card'][data-machine='${mini.id}'] .machine-card__state`);
    expect(state.text()).toBe("reachable · 1 session");
  });

  describe("updates in Machines", () => {
    const MINI_URL = "http://100.64.12.40:2113";
    const downloaded = { status: "staged", latestVersion: "0.59.1", stagedVersion: "0.59.1", checkedAt: "2026-10-20T08:00:00Z", canRestart: true, rule: "idle" };
    const current = { status: "uptodate", latestVersion: "0.59.1", stagedVersion: null, checkedAt: "2026-10-20T08:00:00Z", canRestart: true, rule: "idle" };
    let fetched: string[];

    function setUp(options: { switchOn: boolean; home?: object; mini?: object }): void {
      routes["/api/preferences"] = () => json(options.switchOn ? { FleetUpdates: "true" } : {});
      routes["/api/machine"] = () => json({ ...atlasInfo, webApp: false, version: "0.59.0", capabilities: atlasCapabilities, update: options.home ?? downloaded });
      routes[`${MINI_URL}/api/machine`] = () => json({ ...miniInfo, version: "0.59.0", update: options.mini ?? current });
      routes["/api/update/rollout"] = () => json({}, 404);
      const handler = vi.mocked(fetch).getMockImplementation()!;
      vi.stubGlobal("fetch", vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
        // The API client passes a Request; the machine helpers pass a path or address.
        const target = input instanceof Request ? new URL(input.url).pathname : input;
        fetched.push(String(target));
        return handler(target, init);
      }));
    }

    async function mountIt(): Promise<VueWrapper> {
      const wrapper = mount(MachinesSection);
      await flushPromises();
      await useMachinesStore().refreshUpdates();
      await flushPromises();
      return wrapper;
    }

    const card = (wrapper: VueWrapper, key: string) => wrapper.get(`[data-testid='machine-card'][data-machine='${key}']`);

    beforeEach(() => {
      fetched = [];
    });

    it("looks the same with the switch off and asks nothing about updates", async () => {
      setUp({ switchOn: false });
      const wrapper = await mountIt();

      expect(wrapper.find("[data-testid='machine-version']").exists()).toBe(false);
      expect(wrapper.find("[data-testid='machines-updates']").exists()).toBe(false);
      expect(wrapper.find("[data-testid='update-restart']").exists()).toBe(false);
      expect(fetched.filter((url) => url.includes("/api/update/"))).toEqual([]);
    });

    it("shows a version line on each card, and Restart only on the machine with an update downloaded", async () => {
      setUp({ switchOn: true });
      const wrapper = await mountIt();

      const home = card(wrapper, HOME_MACHINE_KEY);
      expect(home.get("[data-testid='machine-version-text']").text()).toBe("Fleet 0.59.0 → 0.59.1");
      expect(home.get("[data-testid='machine-version-state']").text()).toBe("0.59.1 downloaded · updates itself when its 3 sessions finish");
      expect(home.get("[data-testid='update-restart']").text()).toBe("Restart into 0.59.1");
      expect(home.get("[data-testid='update-restart']").attributes("disabled")).toBeDefined();
      expect(home.find("[data-testid='update-rule-select']").exists()).toBe(true);

      const miniCard = card(wrapper, mini.id);
      expect(miniCard.get("[data-testid='machine-version-text']").text()).toBe("Fleet 0.59.0");
      expect(miniCard.get("[data-testid='machine-version-state']").text()).toBe("up to date");
      expect(miniCard.find("[data-testid='update-restart']").exists()).toBe(false);
      expect(wrapper.findAll("[data-testid='machine-version']")).toHaveLength(2);
    });

    it("says a machine can't be reached with the version last seen, and offers it no rule menu", async () => {
      setUp({ switchOn: true });
      const wrapper = await mountIt();
      routes[`${MINI_URL}/api/sessions`] = () => {
        throw new TypeError("Failed to fetch");
      };
      await useMachinesStore().refreshMachine(mini.id);
      await flushPromises();

      const line = card(wrapper, mini.id);
      expect(line.get("[data-testid='machine-version-state']").text()).toBe("can't be reached · last seen on 0.59.0");
      expect(line.find("[data-testid='update-rule-select']").exists()).toBe(false);
    });

    it("remembers each machine's version on this device", async () => {
      setUp({ switchOn: true });
      await mountIt();
      expect(JSON.parse(localStorage.getItem("fleet.machineVersions") ?? "{}")).toMatchObject({ [HOME_MACHINE_KEY]: "0.59.0", [mini.id]: "0.59.0" });
    });

    it("says what a machine was last seen on when it hasn't answered since the page opened", async () => {
      localStorage.setItem("fleet.machineVersions", JSON.stringify({ [mini.id]: "0.58.2" }));
      setUp({ switchOn: true });
      routes[`${MINI_URL}/api/machine`] = () => {
        throw new TypeError("Failed to fetch");
      };
      routes[`${MINI_URL}/api/sessions`] = () => {
        throw new TypeError("Failed to fetch");
      };
      const wrapper = await mountIt();
      await useMachinesStore().refreshMachine(mini.id);
      await flushPromises();

      const line = card(wrapper, mini.id);
      expect(line.get("[data-testid='machine-version-text']").text()).toBe("Fleet 0.58.2");
      expect(line.get("[data-testid='machine-version-state']").text()).toBe("can't be reached · last seen on 0.58.2");
    });

    it("shows the release line with Update machines now when something can update", async () => {
      setUp({ switchOn: true, mini: downloaded });
      const wrapper = await mountIt();

      const strip = wrapper.get("[data-testid='machines-updates-strip']");
      expect(strip.attributes("data-kind")).toBe("available");
      expect(strip.text()).toContain("Fleet 0.59.1 is out.");
      expect(strip.text()).toContain("2 machines have it downloaded and switch when they restart.");
      expect(strip.find("[data-testid='updates-default-rule']").exists()).toBe(true);
      expect(strip.get("[data-testid='updates-update-machines']").text()).toBe("Update machines now (1)");
    });

    it("says every machine is up to date in one line, and when it last checked", async () => {
      setUp({ switchOn: true, home: current });
      const wrapper = await mountIt();

      const strip = wrapper.get("[data-testid='machines-updates-strip']");
      expect(strip.attributes("data-kind")).toBe("current");
      expect(strip.text()).toContain("Every machine that can be reached is on Fleet 0.59.1.");
      expect(strip.text()).toMatch(/Checked .+ ago\./);
      expect(strip.find("[data-testid='updates-update-machines']").exists()).toBe(false);
      expect(strip.find("[data-testid='updates-check-now']").exists()).toBe(true);
    });

    it("tells where a rollout is, with a Stop after the machine being handled", async () => {
      setUp({ switchOn: true, mini: downloaded });
      routes["/api/update/rollout"] = () => json({
        id: "r1",
        status: "running",
        startedAt: "2026-10-20T09:00:00Z",
        steps: [
          { machineId: mini.id, name: "mini", state: "waiting", waitingSessions: 2, toVersion: "0.59.1" },
          { machineId: HOME_MACHINE_KEY, name: "atlas", state: "pending", waitingSessions: 0, toVersion: "0.59.1" },
        ],
      });
      const wrapper = await mountIt();

      const strip = wrapper.get("[data-testid='machines-updates-strip']");
      expect(strip.attributes("data-kind")).toBe("rolling");
      expect(strip.text()).toContain("Updating machines to 0.59.1, one at a time.");
      expect(strip.text()).toContain("mini is waiting for 2 sessions; atlas is next.");
      expect(strip.get("[data-testid='updates-stop-rollout']").text()).toBe("Stop after mini");
      expect(card(wrapper, mini.id).get("[data-testid='machine-version-state']").text()).toBe("Updating: waiting for 2 sessions to finish");
      expect(card(wrapper, HOME_MACHINE_KEY).get("[data-testid='machine-version-state']").text()).toBe("Updating: next, after mini");
    });

    it("says a machine in the rollout's queue that can't be reached will be skipped, not that it's next", async () => {
      setUp({ switchOn: true, mini: downloaded });
      routes[`${MINI_URL}/api/machine`] = () => {
        throw new TypeError("Failed to fetch");
      };
      routes[`${MINI_URL}/api/sessions`] = () => {
        throw new TypeError("Failed to fetch");
      };
      routes["/api/update/rollout"] = () => json({
        id: "r1",
        status: "running",
        startedAt: "2026-10-20T09:00:00Z",
        steps: [{ machineId: mini.id, name: "mini", state: "pending", waitingSessions: 0, toVersion: "0.59.1" }],
      });
      const wrapper = await mountIt();
      await useMachinesStore().refreshMachine(mini.id);
      await flushPromises();

      expect(wrapper.get("[data-testid='machines-updates-strip']").text()).toContain("mini can't be reached, so it'll be skipped.");
      expect(card(wrapper, mini.id).get("[data-testid='machine-version-state']").text()).toMatch(/^can't be reached/);
    });

    it("says no release was found when no machine knows of one", async () => {
      setUp({ switchOn: true, home: { ...current, latestVersion: null, status: "unknown" }, mini: { ...current, latestVersion: null, status: "unknown" } });
      const wrapper = await mountIt();
      expect(wrapper.get("[data-testid='machines-updates-strip']").text()).toContain("Fleet hasn't found a release yet.");
    });
  });
});

