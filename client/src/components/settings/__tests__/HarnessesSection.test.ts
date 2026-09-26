import { flushPromises, mount } from "@vue/test-utils";
import { createPinia, getActivePinia, setActivePinia } from "pinia";
import { beforeEach, describe, expect, it, vi } from "vitest";
import HarnessesSection from "@/components/settings/HarnessesSection.vue";
import type { HarnessInfo } from "@/api/client";
import { useAppShellStore } from "@/stores/app-shell";
import { useHarnessSetupStore } from "@/stores/harness-setup";

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

    return reply({ error: "unexpected path" }, 404);
  });
}

async function mountHarnessesSection() {
  const wrapper = mount(HarnessesSection, {
    global: {
      plugins: [getActivePinia()!],
    },
  });

  await flushPromises();
  return wrapper;
}

describe("HarnessesSection", () => {
  beforeEach(() => {
    apiFetchMock.mockReset();
    setActivePinia(createPinia());
  });

  it("renders harnesses section", async () => {
    mockApiResponses({});

    const wrapper = await mountHarnessesSection();

    expect(wrapper.text()).toContain("Harnesses");
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
    expect(wrapper.findAll("article").map((card) => card.text().includes("No settings yet"))).toEqual([false, true]);
    expect(wrapper.find("[data-testid='pooled-opencode-mode-setting']").exists()).toBe(false);
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
});
