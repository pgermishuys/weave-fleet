import { flushPromises, mount } from "@vue/test-utils";
import { beforeEach, describe, expect, it, vi } from "vitest";
import HarnessUpdateStrip from "@/components/settings/HarnessUpdateStrip.vue";
import type { HarnessInfo, HarnessUpdateInfo } from "@/api/client";

const mocks = vi.hoisted(() => ({
  apiFetch: vi.fn(),
  refreshAllHarnesses: vi.fn(),
}));

vi.mock("@/lib/api-client", () => ({ apiFetch: mocks.apiFetch }));
vi.mock("@/composables/use-harnesses", () => ({ refreshAllHarnesses: mocks.refreshAllHarnesses }));

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

const COMMAND = "/home/you/.opencode/bin/opencode upgrade 1.18.31";

function openCode(update: HarnessUpdateInfo, overrides: Partial<HarnessInfo> = {}): HarnessInfo {
  return {
    type: "opencode",
    displayName: "OpenCode",
    available: true,
    userEnabled: true,
    state: "ready",
    version: "1.18.20",
    executablePath: "/home/you/.opencode/bin/opencode",
    capabilities,
    update,
    ...overrides,
  };
}

function mountStrip(harness: HarnessInfo) {
  return mount(HarnessUpdateStrip, { props: { harness } });
}

beforeEach(() => {
  mocks.apiFetch.mockReset().mockResolvedValue(new Response(null, { status: 202 }));
  mocks.refreshAllHarnesses.mockReset();
});

describe("HarnessUpdateStrip", () => {
  it("shows nothing when the harness is current", () => {
    const view = mountStrip(openCode({ updateAvailable: false, latestVersion: "1.18.20", command: COMMAND }));

    expect(view.find("[data-testid='harness-update']").exists()).toBe(false);
  });

  it("offers a newer version and starts the update", async () => {
    const view = mountStrip(openCode({ updateAvailable: true, latestVersion: "1.18.31", command: COMMAND }));

    expect(view.text()).toContain("OpenCode 1.18.31 is available. You have 1.18.20.");
    expect(view.text()).toContain(`Fleet runs ${COMMAND} once no session is working.`);
    await view.get("[data-testid='harness-update-start']").trigger("click");
    await flushPromises();

    expect(view.get("[data-testid='harness-update-start']").text()).toBe("Update to 1.18.31");
    expect(mocks.apiFetch).toHaveBeenCalledWith("/api/harnesses/opencode/update", { method: "POST" });
    expect(mocks.refreshAllHarnesses).toHaveBeenCalled();
  });

  it("says a harness that's too old can't start sessions until it's updated", () => {
    const view = mountStrip(openCode(
      { updateAvailable: true, latestVersion: "1.18.31", minimumVersion: "1.15.10", command: COMMAND },
      { available: false, state: "update-needed", version: "1.12.0", reason: "Fleet needs OpenCode 1.15.10 or newer. You have 1.12.0." },
    ));

    expect(view.get("[data-testid='harness-update']").classes()).toContain("harness-update--warn");
    expect(view.text()).toContain("Fleet needs OpenCode 1.15.10 or newer. You have 1.12.0.");
    expect(view.text()).toContain("New sessions can't use OpenCode until you update.");
  });

  it("waits for working sessions, and the wait can be cancelled", async () => {
    const view = mountStrip(openCode({
      updateAvailable: true,
      latestVersion: "1.18.31",
      command: COMMAND,
      job: { phase: "waiting", workingSessions: 2, fromVersion: "1.18.20" },
    }));

    expect(view.text()).toContain("Waiting for 2 working sessions to finish.");
    await view.get("[data-testid='harness-update-cancel']").trigger("click");
    await flushPromises();

    expect(mocks.apiFetch).toHaveBeenCalledWith("/api/harnesses/opencode/update", { method: "DELETE" });
  });

  it("shows a failed update's reason, output and the command to run by hand", () => {
    const view = mountStrip(openCode({
      updateAvailable: true,
      latestVersion: "1.18.31",
      command: COMMAND,
      job: {
        phase: "failed",
        workingSessions: 0,
        message: `The update failed (exit code 1). OpenCode is still on 1.18.20. Run it yourself: ${COMMAND}`,
        output: "npm error code EACCES",
      },
    }));

    expect(view.get("[data-testid='harness-update']").classes()).toContain("harness-update--error");
    expect(view.text()).toContain("The update failed (exit code 1).");
    expect(view.get("[data-testid='harness-update-output']").text()).toBe("npm error code EACCES");
    expect(view.get("code").text()).toBe(COMMAND);
    expect(view.get("[data-testid='harness-update-start']").text()).toBe("Try again");
  });

  it("shows a finished update until it's dismissed", async () => {
    const view = mountStrip(openCode({
      updateAvailable: false,
      latestVersion: "1.18.31",
      command: COMMAND,
      job: { phase: "succeeded", workingSessions: 0, message: "Updated OpenCode from 1.18.20 to 1.18.31.", toVersion: "1.18.31" },
    }, { version: "1.18.31" }));

    expect(view.text()).toContain("Updated OpenCode from 1.18.20 to 1.18.31.");
    await view.get("[data-testid='harness-update-dismiss']").trigger("click");
    await flushPromises();

    expect(mocks.apiFetch).toHaveBeenCalledWith("/api/harnesses/opencode/update", { method: "DELETE" });
  });

  it("shows why the server refused an update", async () => {
    mocks.apiFetch.mockResolvedValue(new Response(JSON.stringify({ error: "OpenCode is already being updated." }), { status: 409 }));
    const view = mountStrip(openCode({ updateAvailable: true, latestVersion: "1.18.31", command: COMMAND }));

    await view.get("[data-testid='harness-update-start']").trigger("click");
    await flushPromises();

    expect(view.text()).toContain("OpenCode is already being updated.");
  });
});
