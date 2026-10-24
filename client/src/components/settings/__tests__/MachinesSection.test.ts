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
  let routes: Record<string, (url?: string, init?: RequestInit) => Response>;

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
    vi.stubGlobal("fetch", vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const route = routes[String(input).split("?")[0]];
      return route ? route(String(input), init) : json({ error: "no route" }, 404);
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
    const downloaded = { status: "staged", latestVersion: "0.59.1", stagedVersion: "0.59.1", checkedAt: new Date(Date.now() - 2 * 3600_000).toISOString(), canRestart: true, rule: "idle" };
    const current = { status: "uptodate", latestVersion: "0.59.1", stagedVersion: null, checkedAt: new Date(Date.now() - 2 * 3600_000).toISOString(), canRestart: true, rule: "idle" };
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

  describe("Add a machine", () => {
    async function openAddForm() {
      routes["/api/machines"] = () => json({ machines: [] });
      const wrapper = mount(MachinesSection);
      await flushPromises();
      await wrapper.find("[data-testid='machines-add']").trigger("click");
      return wrapper;
    }

    it("starts with a pairing code, and keeps the token form under Advanced", async () => {
      const wrapper = await openAddForm();

      expect(wrapper.find("[data-testid='machines-add-mode-code']").attributes("aria-selected")).toBe("true");
      expect(wrapper.find("[data-testid='machines-add-code']").exists()).toBe(true);
      expect(wrapper.find("[data-testid='machines-add-code-help']").text()).toContain("fleet node pair");
      expect(wrapper.find("[data-testid='machines-add-token']").exists()).toBe(false);

      await wrapper.find("[data-testid='machines-add-mode-address']").trigger("click");
      expect(wrapper.find("[data-testid='machines-add-code']").exists()).toBe(false);
      expect(wrapper.find("[data-testid='machines-add-token']").exists()).toBe(false);
      await wrapper.find("[data-testid='machines-add-advanced']").trigger("click");
      expect(wrapper.find("[data-testid='machines-add-token']").exists()).toBe(true);
    });

    it("needs an address and a code before it can add", async () => {
      const wrapper = await openAddForm();
      const submit = wrapper.find("[data-testid='machines-add-submit']");
      expect(submit.attributes("disabled")).toBeDefined();

      await wrapper.find("[data-testid='machines-add-url']").setValue("http://100.64.90.72:2113");
      expect(submit.attributes("disabled")).toBeDefined();
      await wrapper.find("[data-testid='machines-add-code']").setValue("ABCD-EFGH");
      expect(submit.attributes("disabled")).toBeUndefined();
    });

    it("adds the machine with the code and says so", async () => {
      const wrapper = await openAddForm();
      let sent: unknown = null;
      routes["/api/machines/pair"] = (_, init) => {
        sent = JSON.parse(String(init?.body));
        return json({ id: "ffffffffffffffffffffffffffffffff", name: "falcon", baseUrl: "http://100.64.90.72:2113", os: "linux", status: "online", addedAt: "2026-10-24T00:00:00.000Z", lastSeenAt: null, token: "fdt_d1.secret" });
      };

      await wrapper.find("[data-testid='machines-add-url']").setValue("http://100.64.90.72:2113");
      await wrapper.find("[data-testid='machines-add-code']").setValue("ABCD-EFGH");
      await wrapper.find("[data-testid='machines-add-form']").trigger("submit");
      await flushPromises();

      expect(sent).toEqual({ baseUrl: "http://100.64.90.72:2113", code: "ABCD-EFGH" });
      expect(wrapper.text()).toContain("Added falcon");
      expect(wrapper.find("[data-testid='machines-add-form']").exists()).toBe(false);
    });

    it("shows the error from a code that was used", async () => {
      const wrapper = await openAddForm();
      routes["/api/machines/pair"] = () => json({ error: "That code has expired or was already used. Run fleet node pair on the other machine for a new one." }, 400);

      await wrapper.find("[data-testid='machines-add-url']").setValue("http://100.64.90.72:2113");
      await wrapper.find("[data-testid='machines-add-code']").setValue("ABCD-EFGH");
      await wrapper.find("[data-testid='machines-add-form']").trigger("submit");
      await flushPromises();

      expect(wrapper.find("[data-testid='machines-add-error']").text()).toContain("already used");
    });

  describe("Add a machine over SSH", () => {
    const hosts = { hosts: [
      { alias: "falcon", destination: "falcon", hostName: "100.64.90.72", user: "alice", port: null },
      { alias: "atlas", destination: "atlas", hostName: null, user: null, port: null },
    ] };
    const falconMachine = { id: "ffffffffffffffffffffffffffffffff", name: "falcon", baseUrl: "http://100.64.90.72:2113", os: "linux", status: "online", addedAt: "2026-10-24T00:00:00.000Z", lastSeenAt: null, token: "fdt_d1.secret", sshDestination: "alice@falcon", tunnelUp: true };

    async function openForm(withHosts: boolean) {
      routes["/api/machines"] = () => json({ machines: [] });
      routes["/api/machines/ssh-hosts"] = () => json(withHosts ? hosts : { hosts: [] });
      const wrapper = mount(MachinesSection);
      await flushPromises();
      await wrapper.find("[data-testid='machines-add']").trigger("click");
      await flushPromises();
      return wrapper;
    }

    function tabs(wrapper: VueWrapper): string[] {
      return wrapper.findAll("[role='tab']").map((tab) => tab.attributes("data-testid")!);
    }

    it("puts Over SSH first and selected when ~/.ssh/config has hosts", async () => {
      const wrapper = await openForm(true);

      expect(tabs(wrapper)).toEqual(["machines-add-mode-ssh", "machines-add-mode-code", "machines-add-mode-address"]);
      expect(wrapper.find("[data-testid='machines-add-mode-ssh']").attributes("aria-selected")).toBe("true");
      expect(wrapper.find("[data-testid='machines-add-ssh-host']").exists()).toBe(true);
      expect(wrapper.find("[data-testid='machines-add-url']").exists()).toBe(false);
      // The token form is the By address tab's own.
      expect(wrapper.find("[data-testid='machines-add-advanced']").exists()).toBe(false);
      expect(wrapper.findAll("datalist option").map((option) => option.attributes("label"))).toEqual(["falcon (alice@100.64.90.72)", "atlas"]);
    });

    it("keeps the pairing code first and Over SSH last when there are no hosts", async () => {
      const wrapper = await openForm(false);

      expect(tabs(wrapper)).toEqual(["machines-add-mode-code", "machines-add-mode-address", "machines-add-mode-ssh"]);
      expect(wrapper.find("[data-testid='machines-add-mode-code']").attributes("aria-selected")).toBe("true");
      await wrapper.find("[data-testid='machines-add-mode-ssh']").trigger("click");
      expect(wrapper.find("[data-testid='machines-add-ssh-host']").exists()).toBe(true);
    });

    it("sends the typed destination and says it was added", async () => {
      const wrapper = await openForm(true);
      let sent: unknown = null;
      routes["/api/machines/ssh"] = (_, init) => {
        sent = JSON.parse(String(init?.body));
        return json(falconMachine);
      };
      const submit = wrapper.find("[data-testid='machines-add-submit']");
      expect(submit.attributes("disabled")).toBeDefined();

      await wrapper.find("[data-testid='machines-add-ssh-host']").setValue("alice@falcon:2222");
      expect(submit.attributes("disabled")).toBeUndefined();
      await wrapper.find("[data-testid='machines-add-form']").trigger("submit");
      await flushPromises();

      expect(sent).toEqual({ destination: "alice@falcon:2222" });
      expect(wrapper.text()).toContain("Added falcon");
      expect(wrapper.text()).toContain("Over SSH as alice@falcon");
    });

    it("shows the error, including when Fleet isn't installed there", async () => {
      const wrapper = await openForm(true);
      routes["/api/machines/ssh"] = () => json({ error: "Fleet isn't installed there (falcon). Install it first." }, 400);

      await wrapper.find("[data-testid='machines-add-ssh-host']").setValue("falcon");
      await wrapper.find("[data-testid='machines-add-form']").trigger("submit");
      await flushPromises();

      expect(wrapper.find("[data-testid='machines-add-error']").text()).toContain("Fleet isn't installed there");
    });

    const plan = { error: "Fleet isn't installed there.", host: "falcon", os: "linux", canInstall: true, docs: "https://fleet.tryweave.io/docs", commands: [
      "curl -fsSL https://github.com/pgermishuys/fleet-releases/releases/latest/download/install.sh | sh",
      "$HOME/.weave/fleet/bin/fleet node install-service",
    ] };

    async function submitHost(wrapper: VueWrapper, host = "falcon") {
      await wrapper.find("[data-testid='machines-add-ssh-host']").setValue(host);
      await wrapper.find("[data-testid='machines-add-form']").trigger("submit");
      await flushPromises();
    }

    it("shows what installing would run when Fleet isn't there, and runs nothing until the click", async () => {
      const wrapper = await openForm(true);
      let installed = 0;
      routes["/api/machines/ssh"] = () => json(plan, 409);
      routes["/api/machines/ssh/install"] = () => { installed++; return json(falconMachine); };

      await submitHost(wrapper);

      const card = wrapper.find("[data-testid='machines-install-plan']");
      expect(card.exists()).toBe(true);
      expect(wrapper.find("[data-testid='machines-install-commands']").text()).toContain("$ curl -fsSL https://github.com/pgermishuys/fleet-releases/releases/latest/download/install.sh | sh");
      expect(card.text()).toContain("$ $HOME/.weave/fleet/bin/fleet node install-service");
      expect(card.text()).toContain("127.0.0.1 only");
      expect(card.text()).toContain("fleet node uninstall-service");
      expect(card.text()).toContain("Harnesses (OpenCode, Claude Code…) aren't installed");
      expect(card.text()).toContain("Nothing runs until you click");
      expect(wrapper.find("[data-testid='machines-add-error']").exists()).toBe(false);
      expect(installed).toBe(0);
    });

    it("installs and pairs on Install and pair, then says it was added", async () => {
      const wrapper = await openForm(true);
      let sent: unknown = null;
      routes["/api/machines/ssh"] = () => json(plan, 409);
      routes["/api/machines/ssh/install"] = (_, init) => { sent = JSON.parse(String(init?.body)); return json(falconMachine); };
      await submitHost(wrapper, "alice@falcon");

      await wrapper.find("[data-testid='machines-install-run']").trigger("click");
      await flushPromises();

      expect(sent).toEqual({ destination: "alice@falcon" });
      expect(wrapper.text()).toContain("Added falcon");
      expect(wrapper.find("[data-testid='machines-install-plan']").exists()).toBe(false);
    });

    it("goes back on I'll do it myself, and shows an install that failed in plain words", async () => {
      const wrapper = await openForm(true);
      routes["/api/machines/ssh"] = () => json(plan, 409);
      routes["/api/machines/ssh/install"] = () => json({ error: "falcon has no curl." }, 400);
      await submitHost(wrapper);

      await wrapper.find("[data-testid='machines-install-run']").trigger("click");
      await flushPromises();
      expect(wrapper.find("[data-testid='machines-add-error']").text()).toContain("has no curl");
      expect(wrapper.find("[data-testid='machines-install-plan']").exists()).toBe(true);

      await wrapper.find("[data-testid='machines-install-cancel']").trigger("click");
      expect(wrapper.find("[data-testid='machines-install-plan']").exists()).toBe(false);
      expect(wrapper.find("[data-testid='machines-add-submit']").exists()).toBe(true);
    });

    it("offers no install on a Windows machine, only the docs", async () => {
      const wrapper = await openForm(true);
      routes["/api/machines/ssh"] = () => json({ ...plan, os: "unsupported", canInstall: false, commands: [] }, 409);

      await submitHost(wrapper);

      expect(wrapper.find("[data-testid='machines-install-run']").exists()).toBe(false);
      expect(wrapper.find("[data-testid='machines-install-commands']").exists()).toBe(false);
      expect(wrapper.find("[data-testid='machines-install-docs']").attributes("href")).toBe("https://fleet.tryweave.io/docs");
    });

    it("drops the plan when the host is changed", async () => {
      const wrapper = await openForm(true);
      routes["/api/machines/ssh"] = () => json(plan, 409);
      await submitHost(wrapper);
      expect(wrapper.find("[data-testid='machines-install-plan']").exists()).toBe(true);

      await wrapper.find("[data-testid='machines-add-ssh-host']").setValue("atlas");

      expect(wrapper.find("[data-testid='machines-install-plan']").exists()).toBe(false);
    });

    it("says how an SSH machine is reached, and when its tunnel is down", async () => {
      routes["/api/machines"] = () => json({ machines: [falconMachine, { ...falconMachine, id: "cccccccccccccccccccccccccccccccc", name: "osprey", sshDestination: "alice@osprey", tunnelUp: false }] });
      const wrapper = mount(MachinesSection);
      await flushPromises();

      expect(wrapper.findAll("[data-testid='machine-ssh-line']").map((line) => line.text())).toEqual(["Over SSH as alice@falcon", "Over SSH as alice@osprey"]);
      expect(wrapper.findAll("[data-testid='machine-tunnel-down']")).toHaveLength(1);
      expect(wrapper.get("[data-testid='machine-tunnel-down']").text()).toBe("Tunnel down");
    });
  });
  });
});

