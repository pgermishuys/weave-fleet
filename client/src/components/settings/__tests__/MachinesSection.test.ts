import { enableAutoUnmount, flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { createPinia, setActivePinia } from "pinia";
import { HOME_MACHINE_KEY, saveMachines, setActiveMachine, type MachineConnection } from "@/lib/machines";
import { useMachinesStore, type MachineCapabilities, type MachineInfo } from "@/stores/machines";
import { usePreferencesStore } from "@/stores/preferences";
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

  it("shows no version, update rule or Restart even with Updates in Machines on: they are in Updates & about", async () => {
    routes["/api/preferences"] = () => json({ FleetUpdates: "true" });
    routes["/api/machine"] = () => json({ ...atlasInfo, version: "0.59.0", capabilities: atlasCapabilities, update: { status: "staged", latestVersion: "0.59.1", stagedVersion: "0.59.1", checkedAt: null, canRestart: true, rule: "idle" } });
    const wrapper = mount(MachinesSection);
    await flushPromises();

    for (const id of ["machine-version", "machines-updates", "update-restart", "update-rule-select"])
      expect(wrapper.find(`[data-testid='${id}']`).exists()).toBe(false);
  });

  describe("Add a machine", () => {
    async function openAddForm() {
      routes["/api/machines"] = () => json({ machines: [] });
      const wrapper = mount(MachinesSection);
      await flushPromises();
      await wrapper.find("[data-testid='machines-add']").trigger("click");
      return wrapper;
    }

    const falconPreview = { machineId: "ffffffffffffffffffffffffffffffff", name: "falcon", os: "linux", version: "0.65.2", baseUrl: "http://100.64.90.72:2113" };
    const falconMachine = { id: "ffffffffffffffffffffffffffffffff", name: "falcon", baseUrl: "http://100.64.90.72:2113", os: "linux", status: "online", addedAt: "2026-10-24T00:00:00.000Z", lastSeenAt: null, token: "fdt_d1.secret" };
    const link = "http://100.64.90.72:2113/add-machine#ABCD-EFGH";

    async function paste(wrapper: VueWrapper, text: string) {
      await wrapper.find("[data-testid='machines-add-link']").setValue(text);
      await flushPromises();
    }

    it("opens on one Pairing link field, with Over SSH as the other choice", async () => {
      const wrapper = await openAddForm();

      expect(wrapper.findAll("[role='tab']").map((tab) => tab.text())).toEqual(["Pairing link", "Over SSH"]);
      expect(wrapper.find("[data-testid='machines-add-link']").exists()).toBe(true);
      expect(wrapper.find("[data-testid='machines-add-link-help']").text()).toContain("fleet node pair");
      expect(wrapper.find("[data-testid='machines-add-code']").exists()).toBe(false);
      expect(wrapper.find("[data-testid='machines-add-submit']").attributes("disabled")).toBeDefined();
    });

    it("looks at the machine behind a pasted link and says what adding it means", async () => {
      const wrapper = await openAddForm();
      let looked: unknown = null;
      routes["/api/machines/pair/preview"] = (_, init) => {
        looked = JSON.parse(String(init?.body));
        return json(falconPreview);
      };

      await paste(wrapper, link);

      expect(looked).toEqual({ baseUrl: "http://100.64.90.72:2113", code: "ABCD-EFGH" });
      expect(wrapper.find("[data-testid='machines-add-found-line']").text()).toBe("falcon · Linux · Fleet 0.65.2");
      expect(wrapper.find("[data-testid='machines-add-found-reach']").text()).toBe("Reached over Tailscale at 100.64.90.72:2113");
      const note = wrapper.find("[data-testid='machines-add-note']").text();
      expect(note).toContain("run there, with falcon's sign-ins, folders and agents");
      expect(note).toContain("run agents, change files and open terminals on falcon");
      expect(note).toContain("Forget falcon here to stop");
      expect(wrapper.find("[data-testid='machines-add-submit']").text()).toBe("Add falcon");
      expect(wrapper.find("[data-testid='machines-add-allow-agents']").exists()).toBe(false);
    });

    it("adds the machine from the link and says so", async () => {
      const wrapper = await openAddForm();
      let sent: unknown = null;
      routes["/api/machines/pair/preview"] = () => json(falconPreview);
      routes["/api/machines/pair"] = (_, init) => {
        sent = JSON.parse(String(init?.body));
        return json(falconMachine);
      };
      await paste(wrapper, link);

      await wrapper.find("[data-testid='machines-add-form']").trigger("submit");
      await flushPromises();

      expect(sent).toEqual({ baseUrl: "http://100.64.90.72:2113", code: "ABCD-EFGH" });
      expect(wrapper.text()).toContain("Added falcon");
      expect(wrapper.find("[data-testid='machines-add-form']").exists()).toBe(false);
    });

    it("offers to let agents hand work to the machine when that switch is on, and sets it at add time", async () => {
      const wrapper = await openAddForm();
      usePreferencesStore().preferences = { AgentHandoff: "true", SessionMessages: "true" };
      let allowed: unknown = null;
      routes["/api/machines/pair/preview"] = () => json(falconPreview);
      routes["/api/machines/pair"] = () => json(falconMachine);
      routes[`/api/machines/${falconMachine.id}`] = (_, init) => {
        allowed = JSON.parse(String(init?.body));
        return json({ ...falconMachine, agentsAllowed: true });
      };
      await paste(wrapper, link);

      await wrapper.find("[data-testid='machines-add-allow-agents']").setValue(true);
      await wrapper.find("[data-testid='machines-add-form']").trigger("submit");
      await flushPromises();

      expect(allowed).toEqual({ agentsAllowed: true });
    });

    it("takes a bare address, asks for the code, then looks", async () => {
      const wrapper = await openAddForm();
      routes["/api/machines/pair/preview"] = () => json(falconPreview);

      await paste(wrapper, "100.64.90.72:2113");
      expect(wrapper.find("[data-testid='machines-add-code']").exists()).toBe(true);
      expect(wrapper.find("[data-testid='machines-add-found']").exists()).toBe(false);
      expect(wrapper.find("[data-testid='machines-add-submit']").attributes("disabled")).toBeDefined();

      await wrapper.find("[data-testid='machines-add-code']").setValue("abcd-efgh");
      await flushPromises();
      expect(wrapper.find("[data-testid='machines-add-found-line']").text()).toContain("falcon");
      expect(wrapper.find("[data-testid='machines-add-submit']").attributes("disabled")).toBeUndefined();
    });

    it("keeps the master token under Advanced, for an address", async () => {
      const wrapper = await openAddForm();
      let sent: unknown = null;
      routes["/api/machine"] = () => json(atlasInfo);
      routes["http://100.64.90.72:2113/api/machine"] = () => json({ ...atlasInfo, id: falconMachine.id, name: "falcon" });
      routes["/api/machines/import"] = (_, init) => {
        sent = JSON.parse(String(init?.body));
        return json({ machines: [falconMachine] });
      };
      await paste(wrapper, "100.64.90.72:2113");
      expect(wrapper.find("[data-testid='machines-add-token']").exists()).toBe(false);

      await wrapper.find("[data-testid='machines-add-advanced']").trigger("click");
      await wrapper.find("[data-testid='machines-add-token']").setValue("master-token-0123456789");
      expect(wrapper.find("[data-testid='machines-add-submit']").attributes("disabled")).toBeUndefined();
      await wrapper.find("[data-testid='machines-add-form']").trigger("submit");
      await flushPromises();

      expect(sent).toMatchObject({ machines: [{ baseUrl: "http://100.64.90.72:2113", token: "master-token-0123456789" }] });
    });

    it("says why a link can't be used, and why the machine wasn't found", async () => {
      const wrapper = await openAddForm();

      await paste(wrapper, "http://100.64.90.72:2113/add-machine#NOPE");
      expect(wrapper.find("[data-testid='machines-add-link-invalid']").text()).toContain("no valid code");

      routes["/api/machines/pair/preview"] = () => json({ error: "That code has expired or was already used. Run fleet node pair on the other machine for a new one." }, 400);
      await paste(wrapper, link);
      expect(wrapper.find("[data-testid='machines-add-preview-error']").text()).toContain("already used");
      expect(wrapper.find("[data-testid='machines-add-found']").exists()).toBe(false);
      expect(wrapper.find("[data-testid='machines-add-submit']").attributes("disabled")).toBeDefined();
    });

  describe("When a machine isn't answering", () => {
    const refused = {
      code: "fleet-not-running",
      headline: "Fleet isn't running on mini",
      steps: [
        { label: "Name found", state: "ok" },
        { label: "Machine answers", state: "ok" },
        { label: "Port answers", state: "failed", detail: "The connection was refused, so the machine is up and Fleet is not." },
        { label: "It's Fleet", state: "notChecked" },
      ],
      try: "On mini, run fleet node up. It starts Fleet in the background.",
    };
    const miniOnServer = { id: mini.id, name: "mini", baseUrl: mini.baseUrl, os: "macos", status: "unreachable", addedAt: mini.addedAt, lastSeenAt: null, token: mini.token };

    function stopMini(): void {
      routes["/api/machines"] = () => json({ machines: [miniOnServer] });
      routes["http://100.64.12.40:2113/api/sessions"] = () => { throw new TypeError("Failed to fetch"); };
      routes[`/api/machines/${mini.id}/check`] = () => json({ reachable: false, error: "Couldn't reach it.", diagnosis: refused });
    }

    it("names the step that broke on the card, with the steps under Details", async () => {
      stopMini();
      const wrapper = mount(MachinesSection);
      await flushPromises();

      const card = wrapper.get(`[data-testid='machine-card'][data-machine='${mini.id}']`);
      expect(card.get(".machine-card__state").text()).toBe("Fleet isn't running on mini");
      const steps = card.findAll("[data-step-state]").map((step) => [step.attributes("data-step-state"), step.text()]);
      expect(steps.map(([state]) => state)).toEqual(["ok", "ok", "failed", "notChecked"]);
      expect(steps[2]![1]).toContain("Port answers");
      expect(card.get("[data-testid='machine-diagnosis-try']").text()).toBe("Try On mini, run fleet node up. It starts Fleet in the background.");
    });

    it("Retry now reads the machine again and clears the headline once it answers", async () => {
      stopMini();
      const wrapper = mount(MachinesSection);
      await flushPromises();

      routes["http://100.64.12.40:2113/api/sessions"] = () => json([]);
      await wrapper.get("[data-testid='machine-diagnosis-retry']").trigger("click");
      await flushPromises();

      expect(wrapper.find("[data-testid='machine-diagnosis']").exists()).toBe(false);
      expect(wrapper.get(`[data-machine='${mini.id}'] .machine-card__state`).text()).toContain("reachable");
    });

    it("shows the steps under Add a machine when the check fails", async () => {
      routes["/api/machines"] = () => json({ machines: [] });
      routes["/api/machines/pair/preview"] = () => json({ error: "Couldn't reach it.", diagnosis: refused }, 400);
      const wrapper = mount(MachinesSection);
      await flushPromises();
      await wrapper.find("[data-testid='machines-add']").trigger("click");
      await wrapper.find("[data-testid='machines-add-link']").setValue("http://100.64.12.40:2113/add-machine#ABCD-EFGH");
      await flushPromises();

      const error = wrapper.get("[data-testid='machines-add-preview-error']");
      expect(error.text()).toContain("Fleet isn't running on mini");
      expect(error.findAll("[data-step-state]")).toHaveLength(4);
    });
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
      await wrapper.find("[data-testid='machines-add-mode-ssh']").trigger("click");
      return wrapper;
    }

    it("lists the hosts from ~/.ssh/config", async () => {
      const wrapper = await openForm(true);

      expect(wrapper.find("[data-testid='machines-add-mode-ssh']").attributes("aria-selected")).toBe("true");
      expect(wrapper.find("[data-testid='machines-add-ssh-host']").exists()).toBe(true);
      expect(wrapper.find("[data-testid='machines-add-link']").exists()).toBe(false);
      expect(wrapper.findAll("datalist option").map((option) => option.attributes("label"))).toEqual(["falcon (alice@100.64.90.72)", "atlas"]);
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
      "curl -fsSL https://github.com/pgermishuys/weave-fleet/releases/latest/download/install.sh | sh",
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
      expect(wrapper.find("[data-testid='machines-install-commands']").text()).toContain("$ curl -fsSL https://github.com/pgermishuys/weave-fleet/releases/latest/download/install.sh | sh");
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
      expect(wrapper.get("[data-testid='machine-tunnel-down']").text()).toBe("The SSH tunnel to osprey is down");
    });
  });
  });
});

