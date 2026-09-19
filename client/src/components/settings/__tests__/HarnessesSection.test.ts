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
        mode: "Separate from OpenCode 1",
        folders: [
          { label: "Settings", path: "/home/you/.weave/harnesses/opencode2/config" },
          { label: "Sessions", path: "/home/you/.weave/harnesses/opencode2/data/opencode.db" },
        ],
        notes: ["OpenCode 2 keeps its own provider sign-ins here."],
      },
    }), createHarness("pi", "Pi")]);

    const wrapper = await mountHarnessesSection();

    const install = wrapper.get("[data-testid='harness-install']");
    expect(install.get("[data-testid='harness-install-mode']").text()).toBe("Separate from OpenCode 1");
    expect(install.text()).toContain("2.0.9");
    expect(install.text()).toContain("Sessions/home/you/.weave/harnesses/opencode2/data/opencode.db");
    expect(install.text()).toContain("OpenCode 2 keeps its own provider sign-ins here.");
    expect(install.get("[data-testid='harness-install-sign-in']").text()).toContain("OPENCODE_CONFIG_DIR=/c OPENCODE_DB=/d");
    // Pi says nothing about its install, so it keeps the placeholder.
    expect(wrapper.findAll("article").map((card) => card.text().includes("No settings yet"))).toEqual([false, true]);
    expect(wrapper.find("[data-testid='pooled-opencode-mode-setting']").exists()).toBe(false);
  });

  it("offers no sign-in before OpenCode 2 is installed", async () => {
    mockApiResponses({}, () => [createHarness("opencode2", "OpenCode 2", {
      available: false,
      state: "not-installed",
      setup: { installCommand: "curl -fsSL https://opencode.ai/v2/install | bash", signInCommand: "opencode2 auth login", mode: "Default install" },
    })]);

    const wrapper = await mountHarnessesSection();

    expect(wrapper.get("[data-testid='harness-install']").text()).toContain("Not installed yet");
    expect(wrapper.find("[data-testid='harness-install-sign-in']").exists()).toBe(false);
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
