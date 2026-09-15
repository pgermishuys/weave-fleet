import { enableAutoUnmount, flushPromises, mount } from "@vue/test-utils";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import SystemSection from "@/components/settings/SystemSection.vue";
import type { DesktopUpdateState, FleetDesktopBridge } from "@/lib/desktop";

const { getMock } = vi.hoisted(() => ({ getMock: vi.fn() }));

vi.mock("@/api/client", () => ({
  api: { GET: getMock, POST: vi.fn().mockResolvedValue({}) },
}));

function serverStatus(status: string) {
  getMock.mockResolvedValue({
    data: { currentVersion: "0.25.0", status, latestVersion: null, checkedAt: null, error: null, downloadBytesReceived: null, downloadBytesTotal: null },
  });
}

function fakeBridge(initial: DesktopUpdateState) {
  let listener: ((state: DesktopUpdateState) => void) | undefined;
  const bridge: FleetDesktopBridge = {
    version: initial.currentVersion,
    platform: "linux",
    openLogs: vi.fn(),
    retry: vi.fn(),
    getUpdateState: vi.fn().mockResolvedValue(initial),
    onUpdateState: vi.fn((callback) => {
      listener = callback;
      return () => {
        listener = undefined;
      };
    }),
    checkForUpdates: vi.fn().mockResolvedValue({ ...initial, status: "idle" }),
    installUpdate: vi.fn().mockResolvedValue(undefined),
  };
  (window as { fleetDesktop?: FleetDesktopBridge }).fleetDesktop = bridge;
  return { bridge, push: (state: DesktopUpdateState) => listener?.(state) };
}

const idle: DesktopUpdateState = { status: "idle", mode: "install", currentVersion: "0.25.0" };

// The update-status composable shares its state between mounts and only refetches for its first subscriber.
enableAutoUnmount(afterEach);

describe("Settings → System in the desktop app", () => {
  beforeEach(() => {
    getMock.mockReset();
  });

  afterEach(() => {
    delete (window as { fleetDesktop?: FleetDesktopBridge }).fleetDesktop;
  });

  it("shows the app's updates instead of the server's when the app runs Fleet", async () => {
    serverStatus("managed");
    fakeBridge(idle);

    const wrapper = mount(SystemSection);
    await flushPromises();

    expect(wrapper.find('[data-testid="desktop-app-updates"]').exists()).toBe(true);
    expect(wrapper.find('[data-testid="desktop-app-update-status"]').text()).toBe("Up to date");
    expect(wrapper.text()).not.toContain("Updates come from the Fleet app");
  });

  it("offers to restart into a downloaded update", async () => {
    serverStatus("managed");
    const { bridge } = fakeBridge({ ...idle, status: "ready", version: "0.26.0" });

    const wrapper = mount(SystemSection);
    await flushPromises();
    const restart = wrapper.findAll("button").find((button) => button.text() === "Restart to update");
    await restart!.trigger("click");

    expect(wrapper.find('[data-testid="desktop-app-update-status"]').text()).toBe("v0.26.0 is ready");
    expect(bridge.installUpdate).toHaveBeenCalledOnce();
  });

  it("follows the app's update as it downloads", async () => {
    serverStatus("managed");
    const { push } = fakeBridge(idle);

    const wrapper = mount(SystemSection);
    await flushPromises();
    push({ ...idle, status: "downloading", version: "0.26.0", percent: 42 });
    await flushPromises();

    expect(wrapper.find('[data-testid="desktop-app-update-status"]').text()).toBe("Downloading v0.26.0… 42%");
  });

  it("links to the download where the app can't install updates", async () => {
    serverStatus("managed");
    fakeBridge({ ...idle, mode: "notify", status: "available", version: "0.26.0", releaseUrl: "https://example.test/v0.26.0" });

    const wrapper = mount(SystemSection);
    await flushPromises();

    expect(wrapper.findAll("button").some((button) => button.text() === "Download")).toBe(true);
    expect(wrapper.text()).toContain("can't install updates itself yet");
  });

  it("keeps the server's row when the app is connected to a Fleet it didn't start", async () => {
    serverStatus("uptodate");
    fakeBridge(idle);

    const wrapper = mount(SystemSection);
    await flushPromises();

    expect(wrapper.find('[data-testid="desktop-app-updates"]').exists()).toBe(true);
    expect(wrapper.text()).toContain("Up to date");
    expect(wrapper.findAll("button").filter((button) => button.text().includes("Check for updates"))).toHaveLength(2);
  });

  it("has no app row in a browser", async () => {
    serverStatus("managed");

    const wrapper = mount(SystemSection);
    await flushPromises();

    expect(wrapper.find('[data-testid="desktop-app-updates"]').exists()).toBe(false);
    expect(wrapper.text()).toContain("Updates come from the Fleet app");
  });
});
