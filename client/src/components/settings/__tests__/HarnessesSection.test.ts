import { flushPromises, mount } from "@vue/test-utils";
import { createPinia, getActivePinia, setActivePinia } from "pinia";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import HarnessesSection from "@/components/settings/HarnessesSection.vue";
import type { HarnessInfo } from "@/api/client";
import { useAppShellStore } from "@/stores/app-shell";
import { useHarnessSetupStore } from "@/stores/harness-setup";
import { useSettingsNav } from "@/composables/use-settings-nav";

const { apiFetchMock } = vi.hoisted(() => ({
  apiFetchMock: vi.fn(),
}));

vi.mock("@/api/client", () => ({
  api: {
    GET: apiFetchMock,
    POST: apiFetchMock,
    PUT: apiFetchMock,
    DELETE: apiFetchMock,
  },
}));

function reply<T>(body: T, status = 200) {
  const ok = status >= 200 && status < 300;
  return Promise.resolve({
    data: ok ? body : undefined,
    error: ok ? undefined : body,
    response: createJsonResponse(body, status),
  });
}

function createJsonResponse<T>(body: T, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "Content-Type": "application/json" },
  });
}

function createHarness(type: string, displayName: string, overrides: Partial<HarnessInfo> = {}): HarnessInfo {
  return {
    type,
    displayName,
    available: true,
    userEnabled: true,
    state: "ready",
    capabilities: {
      requiresInitialPrompt: true,
      supportsAgents: true,
      supportsModelSelection: true,
      supportsCommands: true,
      supportsForking: true,
      supportsResume: true,
      supportsImageAttachments: true,
      supportsStreaming: true,
      supportsDelegation: true,
    },
    ...overrides,
  };
}

/** What Fleet says each harness loads of Weave; only the harnesses listed here get a Weave group. */
let weaveHarnesses: unknown[] = [];

async function openTab(wrapper: Awaited<ReturnType<typeof mountHarnessesSection>>, id: string): Promise<void> {
  await wrapper.get(`[data-testid='harness-tab-${id}']`).trigger("click");
  await flushPromises();
}

function mockApiResponses(
  preferences: Record<string, string>,
  harnesses: () => HarnessInfo[] = () => [createHarness("opencode", "OpenCode")],
): void {
  apiFetchMock.mockImplementation((path: string) => {
    if (path === "/api/harnesses") {
      return reply(harnesses());
    }

    if (path === "/api/preferences") {
      return reply(preferences);
    }

    if (path === "/api/preferences/{key}") {
      return reply(null);
    }

    if (path === "/api/weave") {
      return reply({ source: "own", files: {}, updatedAt: null, apply: null, harnesses: weaveHarnesses });
    }

    if (path === "/api/agent-browser/settings") {
      return reply({ enabled: true, pages: "session", scripts: false });
    }

    return reply({ error: "unexpected path" }, 404);
  });
}

// The Settings state is shared, so a wrapper left mounted would keep reacting to the next test's links.
const mounted: { unmount: () => void }[] = [];

async function mountHarnessesSection() {
  const wrapper = mount(HarnessesSection, {
    global: {
      plugins: [getActivePinia()!],
    },
  });

  mounted.push(wrapper);
  await flushPromises();
  return wrapper;
}

describe("HarnessesSection", () => {
  afterEach(() => {
    for (const wrapper of mounted.splice(0)) wrapper.unmount();
  });

  beforeEach(() => {
    apiFetchMock.mockReset();
    setActivePinia(createPinia());
    weaveHarnesses = [];
    useSettingsNav().activeHarness.value = null;
    useSettingsNav().activeAnchor.value = null;
  });

  it("renders harnesses section", async () => {
    mockApiResponses({});

    const wrapper = await mountHarnessesSection();

    expect(wrapper.text()).toContain("Default harness");
  });

  it("shows a harness on until the user turns it off, so a computer with only Claude Code works", async () => {
    mockApiResponses({ "opencode.enabled": "false" }, () => [
      createHarness("claude-code", "Claude Code"),
      createHarness("opencode", "OpenCode", { userEnabled: false }),
    ]);

    const wrapper = await mountHarnessesSection();

    const switchOf = async (id: string) => {
      await openTab(wrapper, id);
      return wrapper.get("[data-testid='harness-enabled-switch']").attributes("aria-checked");
    };
    expect(await switchOf("claude-code")).toBe("true");
    expect(await switchOf("opencode")).toBe("false");
  });

  it("says a harness isn't installed, and why", async () => {
    mockApiResponses({}, () => [createHarness("opencode", "OpenCode", {
      available: false,
      state: "not-installed",
      reason: "OpenCode isn't installed: Fleet couldn't find opencode on PATH or in the folders its installer uses.",
    })]);

    const wrapper = await mountHarnessesSection();

    expect(wrapper.text()).toContain("Not installed");
    expect(wrapper.text()).toContain("Fleet couldn't find opencode on PATH");
    expect(wrapper.find("[data-testid='harness-location']").exists()).toBe(false);
  });

  it("shows the version and where Fleet found a harness that needs a sign-in", async () => {
    mockApiResponses({ "claude-code.enabled": "true" }, () => [createHarness("claude-code", "Claude Code", {
      available: false,
      state: "sign-in-required",
      reason: "Claude Code isn't signed in. Run claude auth login.",
      version: "2.1.276",
      executablePath: "/home/you/.local/bin/claude",
    })]);

    const wrapper = await mountHarnessesSection();

    expect(wrapper.text()).toContain("Sign-in needed");
    expect(wrapper.get("[data-testid='harness-location']").text()).toBe("2.1.276 · /home/you/.local/bin/claude");
  });

  it("shows how OpenCode 2 is installed in place of 'No settings yet'", async () => {
    mockApiResponses({ "opencode2.enabled": "true" }, () => [createHarness("opencode2", "OpenCode 2", {
      version: "2.0.9",
      executablePath: "/home/you/.weave/harnesses/opencode2/.opencode/bin/opencode2",
      setup: {
        installCommand: "curl -fsSL https://opencode.ai/v2/install | HOME=/home/you/.weave/harnesses/opencode2 bash -s -- --no-modify-path",
        signInCommand: "OPENCODE_CONFIG_DIR=/c OPENCODE_DB=/d /home/you/.weave/harnesses/opencode2/.opencode/bin/opencode2 auth login",
        mode: "In its own folder",
        folders: [
          { label: "Settings", path: "/home/you/.weave/harnesses/opencode2/config" },
          { label: "Sessions", path: "/home/you/.weave/harnesses/opencode2/data/opencode.db" },
        ],
        notes: ["OpenCode 2 keeps its own provider sign-ins here."],
      },
    }), createHarness("pi", "Pi")]);

    const wrapper = await mountHarnessesSection();

    const install = wrapper.get("[data-testid='harness-install']");
    expect(install.get("[data-testid='harness-install-mode']").text()).toBe("In its own folder");
    expect(install.text()).toContain("2.0.9");
    expect(install.text()).toContain("Sessions/home/you/.weave/harnesses/opencode2/data/opencode.db");
    expect(install.text()).toContain("OpenCode 2 keeps its own provider sign-ins here.");
    expect(install.get("[data-testid='harness-install-sign-in']").text()).toContain("OPENCODE_CONFIG_DIR=/c OPENCODE_DB=/d");
    // Pi says nothing about its install, so it keeps the placeholder.
    expect(wrapper.get("article").text()).not.toContain("No settings yet");
    await openTab(wrapper, "pi");
    expect(wrapper.get("article").text()).toContain("No settings yet");
    expect(wrapper.find("[data-testid^='harness-setting-']").exists()).toBe(false);
  });

  it("shows the harness as it describes itself, the default as the server marks it", async () => {
    mockApiResponses({}, () => [
      createHarness("acme", "Acme Code", {
        isDefault: true,
        presentation: { order: 0, eyebrow: "Cloud harness", description: "An invented harness for tests.", icon: "hexagon" },
      }),
      createHarness("pi", "Pi"),
    ]);

    const wrapper = await mountHarnessesSection();

    const acme = wrapper.get("article");
    expect(acme.text()).toContain("Cloud harness");
    expect(acme.text()).toContain("An invented harness for tests.");
    expect(acme.text()).toContain("Default");
    // One that sends no presentation (an older Fleet) still shows, plainly.
    await openTab(wrapper, "pi");
    expect(wrapper.get("article").text()).toContain("Harness runtime registered by the backend.");
  });

  it("shows a harness's own switches, on by the default it sends until the user sets them", async () => {
    const setting = { key: "acme.fast", label: "Fast mode", description: "Sessions start sooner.", default: true };
    mockApiResponses({}, () => [createHarness("acme", "Acme Code", { settings: [setting] }), createHarness("pi", "Pi")]);

    const wrapper = await mountHarnessesSection();

    const row = wrapper.get("[data-testid='harness-setting-acme.fast']");
    expect(row.text()).toContain("Fast mode");
    expect(row.text()).toContain("On by default. Sessions start sooner.");
    const toggle = wrapper.get("[data-testid='harness-setting-toggle-acme.fast']");
    expect(toggle.attributes("aria-checked")).toBe("true");
    // A harness with switches has settings; one without, and no install to describe, says so.
    expect(wrapper.get("article").text()).not.toContain("No settings yet");

    await toggle.trigger("click");
    await flushPromises();

    expect(apiFetchMock).toHaveBeenCalledWith("/api/preferences/{key}", expect.objectContaining({
      params: { path: { key: "acme.fast" } },
      body: { value: "false" },
    }));
    expect(toggle.attributes("aria-checked")).toBe("false");
  });

  it("lists OpenCode 2's providers when Fleet signs in to them, keeping the terminal as the fallback", async () => {
    const withSignIn = (enabled: boolean) => createHarness("opencode2", "OpenCode 2", {
      version: "2.0.9",
      executablePath: "/home/you/.weave/harnesses/opencode2/.opencode/bin/opencode2",
      capabilities: { ...createHarness("x", "x").capabilities, supportsProviderSignIn: enabled },
      setup: { installCommand: "curl", signInCommand: "opencode2 auth login --standalone", mode: "In its own folder" },
    });
    mockApiResponses({ "opencode2.enabled": "true" }, () => [withSignIn(true)]);
    apiFetchMock.mockImplementation((path: string) => {
      if (path === "/api/harnesses") return reply([withSignIn(true)]);
      if (path === "/api/preferences") return reply({ "opencode2.enabled": "true" });
      if (path === "/api/harnesses/{harnessType}/sign-in") return reply({ note: "Kept in its own database.", providers: [] });
      return reply({ error: "unexpected path" }, 404);
    });

    const wrapper = await mountHarnessesSection();
    await flushPromises();

    expect(wrapper.get("[data-testid='harness-sign-in-note']").text()).toBe("Kept in its own database.");
    expect(wrapper.get("[data-testid='harness-install']").text()).toContain("Or sign in to a provider in a terminal:");
    expect(apiFetchMock).toHaveBeenCalledWith("/api/harnesses/{harnessType}/sign-in", { params: { path: { harnessType: "opencode2" } } });
  });

  it("still lists a harness's sign-ins while it waits for one, so GitHub Copilot can be signed in to from its card", async () => {
    const waiting = createHarness("copilot", "GitHub Copilot", {
      available: false,
      state: "sign-in-required",
      reason: "Sign in to GitHub Copilot.",
      capabilities: { ...createHarness("x", "x").capabilities, supportsProviderSignIn: true },
    });
    apiFetchMock.mockImplementation((path: string) => {
      if (path === "/api/harnesses") return reply([waiting]);
      if (path === "/api/preferences") return reply({ "copilot.enabled": "true" });
      if (path === "/api/harnesses/{harnessType}/sign-in") return reply({ note: null, providers: [] });
      return reply({ error: "unexpected path" }, 404);
    });

    const wrapper = await mountHarnessesSection();
    await flushPromises();

    expect(wrapper.find("[data-testid='harness-sign-in']").exists()).toBe(true);
  });

  it("offers no sign-in from Fleet when the server doesn't (Fleet runs with sign-in)", async () => {
    mockApiResponses({ "opencode2.enabled": "true" }, () => [createHarness("opencode2", "OpenCode 2", {
      version: "2.0.9",
      executablePath: "/home/you/.weave/harnesses/opencode2/.opencode/bin/opencode2",
      setup: { installCommand: "curl", signInCommand: "opencode2 auth login --standalone", mode: "In its own folder" },
    })]);

    const wrapper = await mountHarnessesSection();

    expect(wrapper.find("[data-testid='harness-sign-in']").exists()).toBe(false);
    expect(wrapper.get("[data-testid='harness-install']").text()).toContain("Sign in to a provider in a terminal:");
  });

  it("offers no sign-in before OpenCode 2 is installed, and shows its installer", async () => {
    const separate = "curl -fsSL https://opencode.ai/v2/install | HOME=/home/you/.weave/harnesses/opencode2 bash -s -- --no-modify-path";
    mockApiResponses({}, () => [createHarness("opencode2", "OpenCode 2", {
      available: false,
      state: "not-installed",
      setup: { installCommand: separate, signInCommand: "opencode2 auth login", mode: "In its own folder" },
    })]);

    const wrapper = await mountHarnessesSection();

    const install = wrapper.get("[data-testid='harness-install']");
    expect(install.text()).toContain("Not installed yet");
    expect(install.get("[data-testid='harness-install-command']").text()).toBe(separate);
    expect(install.find("[data-testid='harness-install-sign-in']").exists()).toBe(false);

    await install.get("[data-testid='harness-install-set-up']").trigger("click");
    expect(useHarnessSetupStore().isOpen).toBe(true);
  });

  it("shows both places OpenCode 2 can go, with each one's folders and installer", async () => {
    mockApiResponses({}, () => [createHarness("opencode2", "OpenCode 2", {
      available: false,
      state: "not-installed",
      setup: {
        installCommand: "separate-installer",
        mode: "In its own folder",
        folders: [{ label: "Program", path: "/home/you/.weave/harnesses/opencode2/.opencode/bin" }],
        installChoices: [
          {
            id: "separate",
            label: "In its own folder",
            description: "OpenCode 1 can be installed next to it at any time.",
            command: "separate-installer",
            folders: [{ label: "Program", path: "/home/you/.weave/harnesses/opencode2/.opencode/bin" }],
            recommended: true,
          },
          {
            id: "default",
            label: "As your main opencode",
            description: "Installing OpenCode 1 later replaces OpenCode 2.",
            command: "default-installer",
            folders: [{ label: "Program", path: "/home/you/.opencode/bin" }],
            recommended: false,
          },
        ],
      },
    })]);

    const wrapper = await mountHarnessesSection();

    const install = wrapper.get("[data-testid='harness-install']");
    expect(install.find("[data-testid='harness-install-mode']").exists()).toBe(false);
    expect(install.find("[data-testid='harness-install-command']").exists()).toBe(false);
    const separate = install.get("[data-testid='harness-install-choice-separate']").text();
    expect(separate).toMatch(/In its own folder\s*Recommended/);
    expect(separate).toContain("Program/home/you/.weave/harnesses/opencode2/.opencode/bin");
    expect(separate).toContain("separate-installer");
    const main = install.get("[data-testid='harness-install-choice-default']").text();
    expect(main).toContain("Installing OpenCode 1 later replaces OpenCode 2.");
    expect(main).toContain("Program/home/you/.opencode/bin");
    expect(main).toContain("default-installer");
  });

  it("checks again, so a harness installed a moment ago shows up", async () => {
    let installed = false;
    mockApiResponses({}, () => [installed
      ? createHarness("opencode", "OpenCode", { version: "1.18.30", executablePath: "/home/you/.opencode/bin/opencode" })
      : createHarness("opencode", "OpenCode", { available: false, state: "not-installed", reason: "OpenCode isn't installed." })]);
    const wrapper = await mountHarnessesSection();
    expect(wrapper.text()).toContain("Not installed");

    installed = true;
    await wrapper.get("[data-testid='harnesses-check-again']").trigger("click");
    await flushPromises();

    expect(wrapper.text()).toContain("Ready");
    expect(wrapper.get("[data-testid='harness-location']").text()).toBe("1.18.30 · /home/you/.opencode/bin/opencode");
  });

  it("opens harness setup at the harness step, except in cloud mode", async () => {
    mockApiResponses({});
    const wrapper = await mountHarnessesSection();

    await wrapper.get("[data-testid='harnesses-set-up']").trigger("click");

    expect(useHarnessSetupStore().isOpen).toBe(true);
    expect(useHarnessSetupStore().step).toBe("harnesses");

    useAppShellStore().config = { ...useAppShellStore().config, cloudMode: true };
    await flushPromises();
    expect(wrapper.find("[data-testid='harnesses-set-up']").exists()).toBe(false);
  });

  describe("the GitHub Copilot path field", () => {
    it("saves the typed path and checks again, and shows what Fleet found", async () => {
      let found = false;
      mockApiResponses({ "copilot.enabled": "true" }, () => [createHarness("copilot", "GitHub Copilot", found
        ? { version: "weave-copilot 0.1.0", executablePath: "/opt/weave-copilot" }
        : { available: false, state: "not-installed", reason: "GitHub Copilot isn't installed: Fleet couldn't find weave-copilot on PATH." })]);
      const wrapper = await mountHarnessesSection();
      expect(wrapper.find("[data-testid='harness-path-reason']").text()).toContain("couldn't find weave-copilot");

      found = true;
      await wrapper.find("[data-testid='harness-path-input']").setValue("/opt/weave-copilot");
      await wrapper.find("[data-testid='harness-path-check']").trigger("click");
      await flushPromises();

      expect(apiFetchMock).toHaveBeenCalledWith("/api/preferences/{key}", expect.objectContaining({
        params: { path: { key: "copilot.path" } },
        body: { value: "/opt/weave-copilot" },
      }));
      expect(wrapper.find("[data-testid='harness-path-found']").text()).toContain("weave-copilot 0.1.0 · /opt/weave-copilot");
    });

    it("says which weave-copilot and which Copilot CLI are installed", async () => {
      mockApiResponses({ "copilot.enabled": "true" }, () => [createHarness("copilot", "GitHub Copilot", { version: "0.1.0", cliVersion: "1.0.98" })]);

      const wrapper = await mountHarnessesSection();

      expect(wrapper.text()).toContain("weave-copilot 0.1.0 · Copilot CLI 1.0.98 is installed and ready.");
    });

    it("is not on other harnesses' cards", async () => {
      mockApiResponses({});

      const wrapper = await mountHarnessesSection();

      expect(wrapper.find("[data-testid='harness-path-field']").exists()).toBe(false);
    });
  });

  describe("tabs", () => {
    function withPermissions(type: string, name: string, supported: boolean): HarnessInfo {
      return createHarness(type, name, {
        capabilities: { ...createHarness(type, name).capabilities, supportsPermissionLevels: supported, supportsAgentBrowser: supported },
        presentation: { order: 0, eyebrow: "CLI", description: "", icon: "terminal", permissionModes: { ask: "Asks for most", edits: "Asks for commands", all: "Asks for nothing" } },
      });
    }

    it("has one tab per harness Fleet lists, in its order, on the default harness's first", async () => {
      mockApiResponses({}, () => [createHarness("opencode2", "OpenCode 2", { isDefault: true }), createHarness("opencode", "OpenCode"), createHarness("pi", "Pi")]);

      const wrapper = await mountHarnessesSection();

      expect(wrapper.findAll("[role='tab']").map((tab) => tab.text())).toEqual(["OpenCode 2", "OpenCode", "Pi"]);
      expect(wrapper.get("[data-testid='harness-tab-opencode2']").attributes("aria-selected")).toBe("true");
    });

    it("lists GitHub Copilot only when Fleet does, which is while its Labs switch is on", async () => {
      mockApiResponses({}, () => [createHarness("opencode", "OpenCode")]);
      expect((await mountHarnessesSection()).find("[data-testid='harness-tab-copilot']").exists()).toBe(false);

      mockApiResponses({}, () => [createHarness("opencode", "OpenCode"), createHarness("copilot", "GitHub Copilot")]);
      expect((await mountHarnessesSection()).find("[data-testid='harness-tab-copilot']").exists()).toBe(true);
    });

    it("remembers the tab while Settings is open", async () => {
      mockApiResponses({}, () => [createHarness("opencode", "OpenCode"), createHarness("pi", "Pi")]);
      await openTab(await mountHarnessesSection(), "pi");

      const again = await mountHarnessesSection();

      expect(again.get("[data-testid='harness-tab-pi']").attributes("aria-selected")).toBe("true");
    });

    it("opens the tab a link names", async () => {
      mockApiResponses({}, () => [createHarness("opencode", "OpenCode"), createHarness("pi", "Pi")]);
      useSettingsNav().openHarness("pi", "may-do");

      const wrapper = await mountHarnessesSection();

      expect(wrapper.get("[data-testid='harness-tab-pi']").attributes("aria-selected")).toBe("true");
      expect(wrapper.find("[data-setting-group='may-do']").exists()).toBe(true);
    });

    it("spends the anchor once it has scrolled there, so a later save doesn't pull the page back", async () => {
      mockApiResponses({}, () => [createHarness("opencode", "OpenCode"), createHarness("pi", "Pi")]);
      useSettingsNav().openHarness("pi", "may-do");

      // On the page, so the group can be found and scrolled to.
      mounted.push(mount(HarnessesSection, { global: { plugins: [getActivePinia()!] }, attachTo: document.body }));
      await flushPromises();

      expect(useSettingsNav().activeAnchor.value).toBeNull();
    });

    it("the old Weave id opens the default harness's tab when Weave is asked about there", async () => {
      weaveHarnesses = [
        { harnessType: "opencode", harnessName: "OpenCode", checked: true, installs: [] },
        { harnessType: "opencode2", harnessName: "OpenCode 2", checked: true, installs: [] },
      ];
      mockApiResponses({}, () => [createHarness("opencode", "OpenCode"), createHarness("opencode2", "OpenCode 2"), createHarness("pi", "Pi")]);
      useSettingsNav().setActiveSection("weave");

      const wrapper = await mountHarnessesSection();

      expect(wrapper.get("[data-testid='harness-tab-opencode']").attributes("aria-selected")).toBe("true");
      expect(wrapper.find("[data-setting-group='weave']").exists()).toBe(true);
    });

    it("the old Weave id falls to the first harness with Weave when the default has none", async () => {
      weaveHarnesses = [{ harnessType: "opencode2", harnessName: "OpenCode 2", checked: true, installs: [] }];
      mockApiResponses({}, () => [createHarness("pi", "Pi", { isDefault: true }), createHarness("opencode2", "OpenCode 2")]);
      useSettingsNav().setActiveSection("weave");

      const wrapper = await mountHarnessesSection();

      expect(wrapper.get("[data-testid='harness-tab-opencode2']").attributes("aria-selected")).toBe("true");
    });

    it("shows a Weave group only on harnesses Weave supports", async () => {
      weaveHarnesses = [{ harnessType: "opencode", harnessName: "OpenCode", checked: true, installs: [] }];
      mockApiResponses({}, () => [createHarness("opencode", "OpenCode"), createHarness("pi", "Pi")]);

      const wrapper = await mountHarnessesSection();
      expect(wrapper.find("[data-setting-group='weave']").exists()).toBe(true);
      await openTab(wrapper, "pi");
      expect(wrapper.find("[data-setting-group='weave']").exists()).toBe(false);
    });

    it("sets a harness's own permission level, and back to the default clears it", async () => {
      mockApiResponses({ PermissionLevel: "ask" }, () => [withPermissions("claude-code", "Claude Code", true)]);
      const wrapper = await mountHarnessesSection();
      const select = wrapper.get<HTMLSelectElement>("#permission-harness-claude-code");

      expect(wrapper.get("[data-testid='permission-handed-claude-code']").text()).toBe("Asks for most");
      await select.setValue("edits");
      expect(apiFetchMock).toHaveBeenCalledWith("/api/preferences/{key}", expect.objectContaining({
        params: { path: { key: "PermissionLevel.claude-code" } }, body: { value: "edits" },
      }));
      expect(wrapper.get("[data-testid='permission-handed-claude-code']").text()).toBe("Asks for commands");

      await select.setValue("default");
      expect(apiFetchMock).toHaveBeenCalledWith("/api/preferences/{key}", expect.objectContaining({
        params: { path: { key: "PermissionLevel.claude-code" } }, body: { value: "" },
      }));
    });

    it("says a harness that can't take a level has none to set, and that its browser is off", async () => {
      mockApiResponses({}, () => [withPermissions("pi", "Pi", false)]);

      const wrapper = await mountHarnessesSection();

      expect(wrapper.find("#permission-harness-pi").exists()).toBe(false);
      expect(wrapper.get("[data-testid='permission-handed-pi']").text()).toContain("can't hand Pi a permission level");
      expect(wrapper.get("[data-testid='browser-harness-pi']").text()).toContain("Off: Fleet can't add tools to Pi yet.");
    });

    it("shows the browser as on for a harness that gets it while agents may use one", async () => {
      mockApiResponses({}, () => [withPermissions("opencode2", "OpenCode 2", true)]);

      const wrapper = await mountHarnessesSection();

      expect(wrapper.get("[data-testid='browser-harness-opencode2']").text()).toContain("On");
    });
  });
});
