import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { defineComponent, h } from "vue";
import HarnessSetupRows from "@/components/harness-setup/HarnessSetupRows.vue";
import type { HarnessInfo } from "@/api/client";
import { useAppShellStore } from "@/stores/app-shell";

const mocks = vi.hoisted(() => ({
  createSetupTerminal: vi.fn(),
  closeSetupTerminal: vi.fn(),
  refreshAllHarnesses: vi.fn(),
  put: vi.fn(),
}));

vi.mock("@/lib/terminal-api", () => ({
  SETUP_TERMINALS_PATH: "/api/setup/terminals",
  createSetupTerminal: mocks.createSetupTerminal,
  closeSetupTerminal: mocks.closeSetupTerminal,
}));

vi.mock("@/composables/use-harnesses", () => ({
  refreshAllHarnesses: mocks.refreshAllHarnesses,
}));

vi.mock("@/api/client", () => ({
  api: { GET: vi.fn(async () => ({ data: {}, error: undefined })), PUT: mocks.put },
}));

// xterm doesn't run in jsdom; the stand-in shows what the real terminal was given.
vi.mock("@/components/terminal/TerminalView.vue", () => ({
  default: defineComponent({
    name: "TerminalView",
    props: {
      sessionId: { type: String, default: "" },
      terminalId: { type: String, default: "" },
      basePath: { type: String, default: "" },
      initialInput: { type: String, default: "" },
      shown: Boolean,
    },
    setup: (props) => () => h("div", { "data-testid": "terminal-view" }, `${props.basePath}|${props.terminalId}|${props.initialInput}`),
  }),
}));

const capabilities = {
  requiresInitialPrompt: false,
  supportsAgents: true,
  supportsModelSelection: true,
  supportsCommands: true,
  supportsForking: true,
  supportsResume: true,
  supportsImageAttachments: true,
  supportsStreaming: true,
  supportsDelegation: true,
};

function openCode(overrides: Partial<HarnessInfo> = {}): HarnessInfo {
  return {
    type: "opencode",
    displayName: "OpenCode",
    available: false,
    userEnabled: true,
    state: "not-installed",
    reason: "OpenCode isn't installed.",
    capabilities,
    setup: { installCommand: "curl -fsSL https://opencode.ai/install | bash", docsUrl: "https://opencode.ai/docs" },
    ...overrides,
  };
}

function claudeCode(overrides: Partial<HarnessInfo> = {}): HarnessInfo {
  return {
    type: "claude-code",
    displayName: "Claude Code",
    available: false,
    userEnabled: false,
    state: "not-installed",
    reason: "Claude Code isn't installed.",
    capabilities,
    setup: {
      installCommand: "curl -fsSL https://claude.ai/install.sh | bash",
      signInCommand: "/home/you/.local/bin/claude auth login",
    },
    ...overrides,
  };
}

const pi: HarnessInfo = { type: "pi", displayName: "Pi", available: false, userEnabled: false, state: "not-installed", capabilities };

let wrapper: VueWrapper | null = null;

async function mountRows(harnesses: HarnessInfo[]): Promise<VueWrapper> {
  wrapper = mount(HarnessSetupRows, { props: { harnesses, defaultHarnessType: "opencode" } });
  await flushPromises();
  return wrapper;
}

beforeEach(() => {
  mocks.createSetupTerminal.mockReset().mockResolvedValue({ id: "t_setup", title: "zsh", status: "running", createdAt: "" });
  mocks.closeSetupTerminal.mockReset().mockResolvedValue(undefined);
  mocks.refreshAllHarnesses.mockReset();
  mocks.put.mockReset().mockResolvedValue({ data: undefined, error: undefined });
  useAppShellStore().config = { ...useAppShellStore().config, terminalEnabled: true };
});

afterEach(() => {
  wrapper?.unmount();
  wrapper = null;
  vi.useRealTimers();
});

describe("HarnessSetupRows", () => {
  it("lists the harnesses Fleet can install, the default first", async () => {
    const view = await mountRows([claudeCode(), pi, openCode()]);

    const rows = view.findAll("[data-testid^='harness-setup-row-']").map((row) => row.attributes("data-testid"));
    expect(rows).toEqual(["harness-setup-row-opencode", "harness-setup-row-claude-code"]);
    expect(view.get("[data-testid='harness-setup-install-opencode']").text()).toBe("Install OpenCode");
    expect(view.text()).toContain("Open source. Includes free models");
  });

  it("types the installer into a setup terminal, and turns on a harness that was off", async () => {
    const view = await mountRows([openCode(), claudeCode()]);

    await view.get("[data-testid='harness-setup-install-claude-code']").trigger("click");
    await flushPromises();

    expect(mocks.createSetupTerminal).toHaveBeenCalledWith(100, 14);
    expect(view.get("[data-testid='terminal-view']").text())
      .toBe("/api/setup/terminals|t_setup|curl -fsSL https://claude.ai/install.sh | bash");
    expect(view.text()).toContain("Check the command, then press Enter to run it. Fleet won't run it for you.");
    expect(mocks.put).toHaveBeenCalledWith("/api/preferences/{key}", {
      params: { path: { key: "claude-code.enabled" } },
      body: { value: "true" },
    });
  });

  it("checks the harnesses every few seconds while the terminal is open, and says when it finds one", async () => {
    vi.useFakeTimers();
    const view = await mountRows([openCode()]);
    await view.get("[data-testid='harness-setup-install-opencode']").trigger("click");
    await flushPromises();

    vi.advanceTimersByTime(3000);
    expect(mocks.refreshAllHarnesses).toHaveBeenCalledTimes(1);

    await view.setProps({
      harnesses: [openCode({
        available: true,
        state: "ready",
        reason: null,
        version: "1.18.30",
        executablePath: "/home/you/.opencode/bin/opencode",
      })],
    });

    expect(view.get("[data-testid='harness-setup-found']").text())
      .toBe("Found OpenCode 1.18.30 at /home/you/.opencode/bin/opencode. You don't need to restart Fleet.");
  });

  it("offers the sign-in once Claude Code is installed", async () => {
    const view = await mountRows([claudeCode({ state: "sign-in-required", userEnabled: true, version: "2.1.276" })]);

    await view.get("[data-testid='harness-setup-sign-in-claude-code']").trigger("click");
    await flushPromises();

    expect(view.text()).toContain("Installed. Sign in once so sessions can use your account.");
    expect(view.get("[data-testid='terminal-view']").text()).toContain("/home/you/.local/bin/claude auth login");
  });

  it("offers to update a harness that's too old, with its installer", async () => {
    const view = await mountRows([openCode({
      state: "update-needed",
      version: "1.12.0",
      reason: "Fleet needs OpenCode 1.15.10 or newer. You have 1.12.0.",
    })]);

    expect(view.text()).toContain("Fleet needs OpenCode 1.15.10 or newer. You have 1.12.0.");
    const update = view.get("[data-testid='harness-setup-install-opencode']");
    expect(update.text()).toBe("Update OpenCode");
    await update.trigger("click");
    await flushPromises();

    expect(view.get("[data-testid='terminal-view']").text()).toContain("curl -fsSL https://opencode.ai/install | bash");
  });

  it("shows the command to run elsewhere when terminals are off", async () => {
    useAppShellStore().config = { ...useAppShellStore().config, terminalEnabled: false };
    const view = await mountRows([openCode()]);

    await view.get("[data-testid='harness-setup-install-opencode']").trigger("click");
    await flushPromises();

    expect(mocks.createSetupTerminal).not.toHaveBeenCalled();
    const failure = view.get("[role='alert']");
    expect(failure.text()).toContain("Terminals are turned off in this Fleet.");
    expect(failure.get("code").text()).toBe("curl -fsSL https://opencode.ai/install | bash");
  });

  it("ends the setup terminal when it goes away", async () => {
    const view = await mountRows([openCode()]);
    await view.get("[data-testid='harness-setup-install-opencode']").trigger("click");
    await flushPromises();

    view.unmount();
    wrapper = null;
    await flushPromises();

    expect(mocks.closeSetupTerminal).toHaveBeenCalledWith("t_setup");
  });
});
