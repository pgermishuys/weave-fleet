import { enableAutoUnmount, flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { createPinia, setActivePinia } from "pinia";
import { HOME_MACHINE_KEY, saveMachines, setActiveMachine, type MachineConnection } from "@/lib/machines";
import { useMachinesStore, type MachineCapabilities, type MachineInfo } from "@/stores/machines";
import { usePreferencesStore } from "@/stores/preferences";
import { useSettingsNav } from "@/composables/use-settings-nav";
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

  it("opens the Add a machine form when something else asked for it", async () => {
    const nav = useSettingsNav();
    const wrapper = mount(MachinesSection);
    await flushPromises();
    expect(wrapper.find("[data-testid='machines-add-form']").exists()).toBe(false);

    nav.openAddMachine();
    await flushPromises();

    expect(wrapper.find("[data-testid='machines-add-form']").exists()).toBe(true);
    expect(nav.addMachineRequested.value).toBe(false);
  });

  it("opens the form at once when it was asked for before Machines was shown", async () => {
    useSettingsNav().openAddMachine();
    const wrapper = mount(MachinesSection);
    await flushPromises();

    expect(wrapper.find("[data-testid='machines-add-form']").exists()).toBe(true);
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

    it("opens on the pairing link when Fleet sees no machines, with Set it up from here as the other choice", async () => {
      const wrapper = await openAddForm();

      expect(wrapper.findAll("[role='tab']").map((tab) => tab.text())).toEqual(["Set it up from here", "Paste a pairing link"]);
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

  describe("Set it up from here", () => {
    const candidates = { candidates: [
      { name: "falcon", destination: "falcon.tail9c2e.ts.net", source: "Tailscale", os: "linux" },
      { name: "orion", destination: "orion", source: "ssh config", os: null },
      { name: "vega", destination: "vega.tail9c2e.ts.net", source: "Tailscale", os: "windows" },
      { name: "heron", destination: "heron", source: "ssh config", os: null },
    ], commands: ["uname -s", "curl -fsSL https://example.test/install.sh | sh", "fleet node install-service", "fleet node pair --json"] };
    const falconMachine = { id: "ffffffffffffffffffffffffffffffff", name: "falcon", baseUrl: "http://127.0.0.1:41999", os: "linux", status: "online", addedAt: "2026-10-24T00:00:00.000Z", lastSeenAt: null, token: "fdt_d1.secret", sshDestination: "alice@falcon", tunnelUp: true };
    const probes: Record<string, unknown> = {
      "falcon.tail9c2e.ts.net": { state: "noFleet", os: "linux", version: null, error: null, diagnosis: null },
      orion: { state: "ready", os: "macos", version: "0.65.2", error: null, diagnosis: null },
      heron: { state: "cantLogIn", os: null, version: null, error: "SSH turned your keys away", diagnosis: null },
    };
    const step = (key: string, label: string, state: string, detail: string | null = null) => ({ key, label, state, detail });
    const steps = (states: string[]) => [
      step("login", "Log in over SSH", states[0]!, states[0] === "done" ? "Logged in to falcon · Linux" : null),
      step("install", "Install Fleet", states[1]!),
      step("start", "Start it in the background", states[2]!),
      step("pair", "Pair and add the machine", states[3]!),
    ];

    async function openForm() {
      routes["/api/machines"] = () => json({ machines: [] });
      routes["/api/machines/candidates"] = () => json(candidates);
      routes["/api/machines/candidates/probe"] = (_, init) => json(probes[JSON.parse(String(init?.body)).destination]);
      const wrapper = mount(MachinesSection);
      await flushPromises();
      await wrapper.find("[data-testid='machines-add']").trigger("click");
      await flushPromises();
      return wrapper;
    }

    const rowOf = (wrapper: VueWrapper, name: string) => wrapper.findAll("[data-testid='machines-setup-row']").find((row) => row.text().startsWith(name))!;

    it("is the default card when Fleet sees machines, and says what is on each one", async () => {
      const wrapper = await openForm();

      expect(wrapper.find("[data-testid='machines-add-mode-setup']").attributes("aria-selected")).toBe("true");
      expect(wrapper.find("[data-testid='machines-add-link']").exists()).toBe(false);
      expect(wrapper.findAll("[data-testid='machines-setup-row']").map((row) => row.find("[data-testid='machines-setup-status']").text())).toEqual([
        "Linux · SSH works · no Fleet yet",
        "macOS · Fleet 0.65.2",
        "Windows · Fleet sets machines up over SSH on Linux and macOS only",
        "can't log in over SSH",
      ]);
      expect(wrapper.findAll("[data-testid='machines-setup-action']").map((button) => button.text())).toEqual(["Set up", "Add", "Use a link", "Use a link"]);
    });

    it("does not look at a Windows peer over SSH", async () => {
      const looked: string[] = [];
      const wrapper = await openForm();
      routes["/api/machines/candidates/probe"] = (_, init) => { looked.push(JSON.parse(String(init?.body)).destination); return json(probes.orion); };
      await wrapper.find("[data-testid='machines-setup-search']").setValue("vega");
      await flushPromises();

      expect(looked).toEqual([]);
    });

    it("sets a machine up with live steps, shows the commands, and says it was added", async () => {
      const wrapper = await openForm();
      let polls = 0;
      routes["/api/machines/setup"] = (_, init) => {
        expect(JSON.parse(String(init?.body))).toEqual({ destination: "falcon.tail9c2e.ts.net" });
        return json({ id: "job1", destination: "falcon", status: "running", steps: steps(["running", "pending", "pending", "pending"]), machine: null, error: null, diagnosis: null });
      };
      routes["/api/machines/setup/job1"] = () => {
        polls++;
        return polls === 1
          ? json({ id: "job1", destination: "falcon", status: "running", steps: steps(["done", "running", "pending", "pending"]), machine: null, error: null, diagnosis: null })
          : json({ id: "job1", destination: "falcon", status: "done", steps: steps(["done", "done", "done", "done"]), machine: falconMachine, error: null, diagnosis: null });
      };

      await rowOf(wrapper, "falcon").find("[data-testid='machines-setup-action']").trigger("click");
      await flushPromises();

      expect(wrapper.find("[data-testid='machines-setup-title']").text()).toBe("Setting up falcon");
      expect(wrapper.find("[data-testid='machines-add-cancel']").text()).toBe("Close");
      expect(wrapper.findAll("[data-testid='machines-setup-steps'] li").map((li) => li.attributes("data-state"))).toEqual(["running", "pending", "pending", "pending"]);
      expect(wrapper.find("[data-testid='machines-add-note']").text()).toContain("This Fleet can run agents, change files and open terminals on falcon.");
      await wrapper.find("[data-testid='machines-setup-commands-toggle']").trigger("click");
      expect(wrapper.find("[data-testid='machines-setup-commands']").text()).toContain("$ curl -fsSL https://example.test/install.sh | sh");

      await new Promise((resolve) => setTimeout(resolve, 1500));
      await flushPromises();

      expect(wrapper.find("[data-testid='machines-add-form']").exists()).toBe(false);
      expect(wrapper.text()).toContain("Added falcon. Its sessions are in the sessions list.");
    });

    it("names the step that broke and offers the link and a retry", async () => {
      const wrapper = await openForm();
      const diagnosis = { code: "install-no-curl", headline: "falcon has no curl", steps: [{ label: "SSH reaches it", state: "ok" }, { label: "SSH logs in", state: "ok" }, { label: "Fleet installs", state: "failed", detail: "The installer needs curl." }, { label: "Fleet starts", state: "notChecked" }], try: "Install curl on falcon." };
      routes["/api/machines/setup"] = () => json({ id: "job2", destination: "falcon", status: "failed", steps: steps(["done", "failed", "pending", "pending"]), machine: null, error: "falcon has no curl.", diagnosis });

      await rowOf(wrapper, "falcon").find("[data-testid='machines-setup-action']").trigger("click");
      await flushPromises();

      expect(wrapper.find("[data-testid='machines-setup-error']").text()).toContain("falcon has no curl");
      expect(wrapper.find("[data-testid='machines-setup-error']").text()).toContain("Fleet installs");
      expect(wrapper.find("[data-testid='machines-setup-retry']").exists()).toBe(true);

      await wrapper.find("[data-testid='machines-setup-use-link']").trigger("click");
      expect(wrapper.find("[data-testid='machines-add-mode-link']").attributes("aria-selected")).toBe("true");
      expect(wrapper.find("[data-testid='machines-add-link-hint']").text()).toContain("On falcon, run fleet node up");
    });

    it("sends a machine it can't log in to to the pairing link", async () => {
      const wrapper = await openForm();

      await rowOf(wrapper, "heron").find("[data-testid='machines-setup-action']").trigger("click");

      expect(wrapper.find("[data-testid='machines-add-link']").exists()).toBe(true);
      expect(wrapper.find("[data-testid='machines-add-link-hint']").text()).toContain("heron");
    });

    it("offers a typed user@host as a row", async () => {
      const wrapper = await openForm();
      routes["/api/machines/candidates/probe"] = () => json(probes.orion);

      await wrapper.find("[data-testid='machines-setup-search']").setValue("pat@lark");
      await flushPromises();

      expect(wrapper.findAll("[data-testid='machines-setup-row']").map((row) => row.text())).toEqual([expect.stringContaining("pat@lark")]);
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

