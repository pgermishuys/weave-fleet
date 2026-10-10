import { enableAutoUnmount, flushPromises, mount } from "@vue/test-utils";
import { createPinia, setActivePinia } from "pinia";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { defineComponent, shallowRef } from "vue";
import type { DesktopUpdateState, FleetDesktopBridge } from "@/lib/desktop";
import type { UpdateStatus } from "@/composables/use-update-status";
import type { ReleaseNote } from "@/lib/release-notes";

const { getMock, serverStatus, openWhatsNew, savedNotes } = vi.hoisted(() => ({
  getMock: vi.fn(),
  serverStatus: { value: null as unknown },
  openWhatsNew: vi.fn(),
  /** What GET /api/update/releases has: the notes the server saved. */
  savedNotes: { releases: [] as ReleaseNote[], loaded: { value: [] as ReleaseNote[] } },
}));

vi.mock("@/api/client", () => ({ api: { GET: getMock } }));
vi.mock("@/composables/use-whats-new", () => ({ useWhatsNew: () => ({ openWhatsNew }) }));
vi.mock("@/composables/use-release-notes", async () => {
  const { shallowRef: ref } = await import("vue");
  const releases = ref<ReleaseNote[]>([]);
  savedNotes.loaded = releases;
  return {
    useReleaseNotes: () => ({
      releases,
      load: async () => {
        releases.value = savedNotes.releases;
      },
    }),
  };
});
vi.mock("@/composables/use-update-status", async () => {
  const { shallowRef: ref } = await import("vue");
  const status = ref<UpdateStatus | null>(null);
  serverStatus.value = status;
  return { useUpdateStatus: () => ({ updateStatus: status }) };
});

const { isNewerVersion, useUpdateNotices } = await import("@/composables/use-update-notices");
const { useNoticesStore } = await import("@/stores/notices");

enableAutoUnmount(afterEach);

const server = () => serverStatus.value as ReturnType<typeof shallowRef<UpdateStatus | null>>;

function fakeBridge(initial: DesktopUpdateState) {
  let listener: ((state: DesktopUpdateState) => void) | undefined;
  let showUpdate: (() => void) | undefined;
  const bridge: FleetDesktopBridge = {
    version: initial.currentVersion,
    platform: "linux",
    openLogs: vi.fn(),
    retry: vi.fn(),
    getUpdateState: vi.fn().mockResolvedValue(initial),
    onUpdateState: vi.fn((callback) => {
      listener = callback;
      return () => (listener = undefined);
    }),
    checkForUpdates: vi.fn(),
    installUpdate: vi.fn().mockResolvedValue(undefined),
    onShowUpdate: vi.fn((callback) => {
      showUpdate = callback;
      return () => (showUpdate = undefined);
    }),
  };
  (window as { fleetDesktop?: FleetDesktopBridge }).fleetDesktop = bridge;
  return { bridge, push: (state: DesktopUpdateState) => listener?.(state), showUpdate: () => showUpdate?.() };
}

function workingSessions(count: number) {
  getMock.mockResolvedValue({ data: { workingSessions: count } });
}

const Host = defineComponent({
  setup() {
    useUpdateNotices();
    return () => null;
  },
});

async function mountHost() {
  mount(Host);
  await flushPromises();
  return useNoticesStore();
}

async function clickAction(label: string) {
  const store = useNoticesStore();
  const action = store.open?.actions?.find((item) => item.label === label);
  if (!action) throw new Error(`No "${label}" action on ${JSON.stringify(store.open?.title)}`);
  await action.run();
  await flushPromises();
}

const ready: DesktopUpdateState = { status: "ready", mode: "install", currentVersion: "0.36.1", version: "0.37.0" };

function notes(version: string, lines: string[]): ReleaseNote {
  const body = `## What's Changed\n${lines.map((line, i) => `* ${line} by @someone in https://github.com/o/r/pull/${i + 1}`).join("\n")}`;
  return { version, publishedAt: "2026-10-09T08:00:00Z", body, url: `https://example.test/v${version}` };
}

const v037 = notes("0.37.0", [
  "feat(conversation): open files a reply names in a tab",
  "fix(files): show images as a picture",
  "fix(claude-code): keep what the agent left running",
  "fix(analytics): open on a range that ends today",
  "fix: line up a lone session's title",
  "test(preview): hold the stranger port",
]);
const v0361 = notes("0.36.1", ["feat(composer): pick a skill", "fix: a reply so far"]);

describe("update notices", () => {
  beforeEach(() => {
    localStorage.clear();
    setActivePinia(createPinia());
    getMock.mockReset();
    openWhatsNew.mockReset();
    server().value = null;
    savedNotes.releases = [];
    savedNotes.loaded.value = [];
  });

  afterEach(() => {
    delete (window as { fleetDesktop?: FleetDesktopBridge }).fleetDesktop;
  });

  describe("in the desktop app", () => {
    it("says a downloaded update is ready, with a Restart, a Later and the release notes", async () => {
      fakeBridge(ready);
      const store = await mountHost();

      expect(store.nextWaiting).toBe("update:app:0.37.0");
      store.openNext();
      expect(store.open).toMatchObject({
        title: "Fleet 0.37.0 is ready",
        body: "Installs when you restart.",
        chip: "Fleet 0.37.0 ready",
        link: { label: "What's new", href: "https://github.com/pgermishuys/fleet-releases/releases/tag/v0.37.0" },
      });
      expect(store.open?.actions?.map((action) => action.label)).toEqual(["Restart", "Later"]);
    });

    it("opens What's new in Fleet at that version, not GitHub", async () => {
      fakeBridge(ready);
      const store = await mountHost();
      store.openNext();

      store.open?.link?.run?.();

      expect(openWhatsNew).toHaveBeenCalledWith("0.37.0");
    });

    it("says nothing while an update downloads", async () => {
      fakeBridge({ status: "downloading", mode: "install", currentVersion: "0.36.1", version: "0.37.0", percent: 40 });
      const store = await mountHost();

      expect(store.notices).toHaveLength(0);
    });

    it("restarts straight away when no session is working, and lets the app check again", async () => {
      workingSessions(0);
      const { bridge } = fakeBridge(ready);
      const store = await mountHost();
      store.openNext();

      await clickAction("Restart");

      expect(bridge.installUpdate).toHaveBeenCalledWith({ confirmed: false });
    });

    it("asks in the card before a restart stops working sessions", async () => {
      workingSessions(2);
      const { bridge } = fakeBridge(ready);
      const store = await mountHost();
      store.openNext();

      await clickAction("Restart");

      expect(bridge.installUpdate).not.toHaveBeenCalled();
      expect(store.open).toMatchObject({ title: "2 sessions are working", tone: "warn" });
      expect(store.open?.actions?.map((action) => action.label)).toEqual(["Restart anyway", "Cancel"]);

      await clickAction("Cancel");
      expect(store.open?.title).toBe("Fleet 0.37.0 is ready");

      await clickAction("Restart");
      await clickAction("Restart anyway");
      expect(bridge.installUpdate).toHaveBeenCalledWith({ confirmed: true });
    });

    it("offers a download where the app can't install updates itself", async () => {
      const { bridge } = fakeBridge({ status: "available", mode: "notify", currentVersion: "0.36.1", version: "0.37.0" });
      const store = await mountHost();
      store.openNext();

      expect(store.open).toMatchObject({ title: "Fleet 0.37.0 is out", chip: "Fleet 0.37.0 available" });
      await clickAction("Download");

      expect(bridge.installUpdate).toHaveBeenCalledWith();
      expect(store.open).toBeNull();
      expect(store.chips).toHaveLength(1);
    });

    it("replaces the notice when a newer version arrives, and keeps it through a failed re-check", async () => {
      const { push } = fakeBridge(ready);
      const store = await mountHost();

      push({ ...ready, status: "error", error: "offline" });
      expect(store.notices.map((notice) => notice.id)).toEqual(["update:app:0.37.0"]);

      push({ ...ready, version: "0.37.1" });
      expect(store.notices.map((notice) => notice.id)).toEqual(["update:app:0.37.1"]);

      push({ status: "idle", mode: "install", currentVersion: "0.37.1" });
      expect(store.notices).toHaveLength(0);
    });

    it("opens the card again when Check for Updates… finds the update", async () => {
      const { showUpdate } = fakeBridge(ready);
      const store = await mountHost();
      store.openNext();
      store.settle();

      showUpdate();

      expect(store.open?.id).toBe("update:app:0.37.0");
      expect(store.pinned).toBe(true);
    });

    it("opens What's new once after restarting into a newer version, with the changes most worth a look", async () => {
      savedNotes.releases = [v037, v0361];
      localStorage.setItem("weave:last-version:app", "0.36.1");
      fakeBridge({ status: "idle", mode: "install", currentVersion: "0.37.0" });
      const store = await mountHost();

      expect(store.nextWaiting).toBe("whats-new:app:0.37.0");
      store.openNext();
      expect(store.open).toMatchObject({
        title: "What's new in Fleet 0.37",
        chip: "What's new in 0.37",
        more: "+2 more fixes",
        holdMs: 20_000,
      });
      expect(store.open?.body).toMatch(/^Updated from 0\.36\.1 · /);
      expect(store.open?.items).toEqual([
        { kind: "new", label: "Conversation", text: "Open files a reply names in a tab" },
        { kind: "fixed", label: "Files", text: "Show images as a picture" },
        { kind: "fixed", label: "Claude Code", text: "Keep what the agent left running" },
      ]);
      expect(store.open?.actions?.map((action) => action.label)).toEqual(["See all changes", "Close"]);

      await clickAction("See all changes");
      expect(openWhatsNew).toHaveBeenCalledWith("0.37.0", {});
      expect(store.open).toBeNull();
      expect(store.chips.map((chip) => chip.chip)).toEqual(["What's new in 0.37"]);
    });

    it("shows What's new once per version: not again on the next start", async () => {
      savedNotes.releases = [v037];
      localStorage.setItem("weave:last-version:app", "0.36.1");
      fakeBridge({ status: "idle", mode: "install", currentVersion: "0.37.0" });
      const first = mount(Host);
      await flushPromises();
      useNoticesStore().openNext();
      useNoticesStore().settle();
      first.unmount();

      // Fleet starts again: a new page, the same device.
      const pinia = createPinia();
      setActivePinia(pinia);
      mount(Host, { global: { plugins: [pinia] } });
      await flushPromises();

      expect(useNoticesStore(pinia).notices).toHaveLength(0);
    });

    it("covers every version an update skipped over in one card", async () => {
      savedNotes.releases = [v037, v0361, notes("0.36.0", ["feat: old"])];
      localStorage.setItem("weave:last-version:app", "0.36.0");
      fakeBridge({ status: "idle", mode: "install", currentVersion: "0.37.0" });
      const store = await mountHost();
      store.openNext();

      expect(store.open).toMatchObject({
        title: "What's new since Fleet 0.36.0",
        body: "2 updates · 0.36.1 and 0.37.0",
        more: "+4 more in 0.36.1 and 0.37.0",
      });
      expect(store.open?.items?.map((item) => item.text)).toEqual(["Open files a reply names in a tab", "Pick a skill", "Show images as a picture"]);

      await clickAction("See all changes");
      expect(openWhatsNew).toHaveBeenCalledWith("0.37.0", { since: "0.36.0" });
    });

    it("says only \"Updated to …\" when the notes for the new version aren't there", async () => {
      savedNotes.releases = [v0361];
      localStorage.setItem("weave:last-version:app", "0.36.1");
      fakeBridge({ status: "idle", mode: "install", currentVersion: "0.37.0" });
      const store = await mountHost();

      expect(store.nextWaiting).toBeNull();
      expect(store.chips).toEqual([expect.objectContaining({ id: "updated:app:0.37.0", quiet: true })]);
    });

    it("counts what's new on the ready card's link once the notes are in", async () => {
      savedNotes.releases = [v037];
      fakeBridge(ready);
      const store = await mountHost();
      store.openNext();

      expect(store.open?.link?.label).toBe("What's new: 1 new, 4 fixed");
      expect(store.pinned).toBe(false);
    });

    it("says it updated after restarting into a newer version, then goes away", async () => {
      vi.useFakeTimers({ toFake: ["setTimeout", "clearTimeout"] });
      localStorage.setItem("weave:last-version:app", "0.36.1");
      fakeBridge({ status: "idle", mode: "install", currentVersion: "0.37.0" });
      const store = await mountHost();

      expect(store.chips).toEqual([expect.objectContaining({ id: "updated:app:0.37.0", chip: "Updated to 0.37.0", quiet: true })]);
      expect(store.nextWaiting).toBeNull();
      store.chips[0].onChipClick?.();
      expect(openWhatsNew).toHaveBeenCalledWith("0.37.0");

      vi.advanceTimersByTime(60_000);
      expect(store.chips).toHaveLength(0);
      vi.useRealTimers();
    });

    it("says nothing on the first run or the same version", async () => {
      fakeBridge({ status: "idle", mode: "install", currentVersion: "0.37.0" });
      const store = await mountHost();
      expect(store.notices).toHaveLength(0);
      expect(localStorage.getItem("weave:last-version:app")).toBe("0.37.0");
    });
  });

  describe("with the fleet CLI", () => {
    const status = (overrides: Partial<UpdateStatus>): UpdateStatus => ({
      currentVersion: "0.36.1",
      status: "uptodate",
      latestVersion: null,
      checkedAt: null,
      error: null,
      downloadBytesReceived: null,
      downloadBytesTotal: null,
      ...overrides,
    });

    it("says a downloaded update installs the next time Fleet starts", async () => {
      const store = await mountHost();
      server().value = status({ status: "staged", latestVersion: "0.37.0" });
      await flushPromises();

      store.openNext();
      expect(store.open).toMatchObject({ title: "Fleet 0.37.0 is ready", body: "It installs the next time you start Fleet." });
      expect(store.open?.actions?.map((action) => action.label)).toEqual(["Got it"]);
    });

    it("says nothing while it downloads, or when the desktop app manages updates", async () => {
      const store = await mountHost();
      server().value = status({ status: "downloading", latestVersion: "0.37.0" });
      await flushPromises();
      server().value = status({ status: "managed" });
      await flushPromises();

      expect(store.notices).toHaveLength(0);
    });

    it("opens What's new after Fleet restarts into the new version", async () => {
      savedNotes.releases = [v037];
      localStorage.setItem("weave:last-version:server", "0.36.1");
      const store = await mountHost();
      server().value = status({ currentVersion: "0.37.0" });
      await flushPromises();

      expect(store.nextWaiting).toBe("whats-new:server:0.37.0");
    });

    it("says it updated after Fleet restarts into the new version", async () => {
      localStorage.setItem("weave:last-version:server", "0.36.1");
      const store = await mountHost();
      server().value = status({ currentVersion: "0.37.0" });
      await flushPromises();

      expect(store.chips.map((notice) => notice.chip)).toEqual(["Updated to 0.37.0"]);
    });
  });

  it("compares versions by number", () => {
    expect(isNewerVersion("0.37.0", "0.36.1")).toBe(true);
    expect(isNewerVersion("0.10.0", "0.9.9")).toBe(true);
    expect(isNewerVersion("v1.0.0", "0.99.0")).toBe(true);
    expect(isNewerVersion("0.36.1", "0.36.1")).toBe(false);
    expect(isNewerVersion("0.36.0", "0.36.1")).toBe(false);
  });
});
