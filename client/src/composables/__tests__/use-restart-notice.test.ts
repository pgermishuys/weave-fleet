import { enableAutoUnmount, flushPromises, mount } from "@vue/test-utils";
import { createPinia, setActivePinia } from "pinia";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { defineComponent, nextTick } from "vue";
import type { SessionListItem } from "@/api/client";
import type { MachineEntry } from "@/stores/machines";

const { postMock, deleteMock } = vi.hoisted(() => ({ postMock: vi.fn(), deleteMock: vi.fn() }));

vi.mock("@/api/client", async (importOriginal) => ({
  ...(await importOriginal<typeof import("@/api/client")>()),
  api: { POST: postMock, DELETE: deleteMock },
}));

const { useRestartNotice, restartTitle, otherMachineLines } = await import("@/composables/use-restart-notice");
const { updatedToVersion } = await import("@/composables/use-update-notices");
const { useNoticesStore } = await import("@/stores/notices");
const { useSessionsStore } = await import("@/stores/sessions");
const { usePreferencesStore } = await import("@/stores/preferences");

enableAutoUnmount(afterEach);

let pinia: ReturnType<typeof createPinia>;

const Host = defineComponent({
  setup() {
    useRestartNotice();
    return () => null;
  },
});

function item(id: string, title: string, waiting = false, cut = true, auto = false): SessionListItem {
  return {
    session: { id, title },
    interrupted: cut ? { at: "2026-11-06T09:00:00Z", waiting, lastCall: waiting ? null : "bash", changedFiles: 2, auto } : null,
  } as unknown as SessionListItem;
}

const restartNotice = () => useNoticesStore().notices.find((notice) => notice.id.startsWith("restart-cut-off:"));

describe("useRestartNotice", () => {
  beforeEach(() => {
    pinia = createPinia();
    setActivePinia(pinia);
    postMock.mockReset().mockResolvedValue({ response: { ok: true } });
    deleteMock.mockReset().mockResolvedValue({ response: { ok: true } });
    updatedToVersion.value = null;
  });

  it("says Fleet restarted and lists the cut-off sessions, noting a lost question", async () => {
    useSessionsStore().setSessions([item("a", "Fix login"), item("b", "Write docs", true), item("c", "Idle one", false, false)]);
    mount(Host, { global: { plugins: [pinia] } });
    await nextTick();

    const notice = restartNotice()!;
    expect(notice.title).toBe("Fleet restarted");
    expect(notice.body).toBe("2 sessions were cut off.");
    expect(notice.items?.map((i) => i.text)).toEqual(["Fix login", "Write docs: the question was lost. Ask again."]);
    expect(notice.actions?.map((a) => a.label)).toEqual(["Continue all", "Dismiss all"]);
  });

  it("names the version when the restart was an update", async () => {
    useSessionsStore().setSessions([item("a", "Fix login")]);
    mount(Host, { global: { plugins: [pinia] } });
    updatedToVersion.value = "0.66.0";
    await nextTick();

    expect(restartNotice()!.title).toBe("Fleet updated to v0.66.0");
    expect(restartNotice()!.body).toBe("1 session was cut off.");
    expect(restartTitle("v0.66.0")).toBe("Fleet updated to v0.66.0");
  });

  it("Continue all continues every cut-off session and removes the notice", async () => {
    useSessionsStore().setSessions([item("a", "One"), item("b", "Two")]);
    mount(Host, { global: { plugins: [pinia] } });
    await nextTick();

    await restartNotice()!.actions![0]!.run();
    await flushPromises();

    expect(postMock).toHaveBeenCalledTimes(2);
    expect(postMock).toHaveBeenCalledWith("/api/sessions/{id}/interruption/continue", { params: { path: { id: "a" } } });
    expect(deleteMock).not.toHaveBeenCalled();
    expect(restartNotice()).toBeUndefined();
  });

  it("Dismiss all sends nothing to the agents", async () => {
    useSessionsStore().setSessions([item("a", "One"), item("b", "Two")]);
    mount(Host, { global: { plugins: [pinia] } });
    await nextTick();

    await restartNotice()!.actions![1]!.run();

    expect(deleteMock).toHaveBeenCalledTimes(2);
    expect(postMock).not.toHaveBeenCalled();
    expect(restartNotice()).toBeUndefined();
  });

  it("goes away when the last session is continued from its own card", async () => {
    const store = useSessionsStore();
    store.setSessions([item("a", "One")]);
    mount(Host, { global: { plugins: [pinia] } });
    await nextTick();
    expect(restartNotice()).toBeDefined();

    store.setSessions([item("a", "One", false, false)]);
    await nextTick();
    expect(restartNotice()).toBeUndefined();
  });

  it("offers the automatic switch in one line while it is off, and the line turns it on", async () => {
    const preferences = usePreferencesStore();
    const set = vi.spyOn(preferences, "set").mockResolvedValue();
    useSessionsStore().setSessions([item("a", "One")]);
    mount(Host, { global: { plugins: [pinia] } });
    await nextTick();

    const link = restartNotice()!.link!;
    expect(link.label).toBe("Next time, continue these by itself?");
    link.run!();
    expect(set).toHaveBeenCalledWith("RestartContinueAuto", "true");
  });

  it("doesn't offer the switch once it is on", async () => {
    const preferences = usePreferencesStore();
    preferences.preferences = { RestartContinueAuto: "true" };
    useSessionsStore().setSessions([item("a", "One")]);
    mount(Host, { global: { plugins: [pinia] } });
    await nextTick();

    expect(restartNotice()!.link).toBeUndefined();
  });

  it("in automatic mode says Fleet is continuing, with Stop all instead of Continue all", async () => {
    useSessionsStore().setSessions([item("a", "One", false, true, true), item("b", "Two", false, true, true), item("c", "Three", true)]);
    mount(Host, { global: { plugins: [pinia] } });
    await nextTick();

    const notice = restartNotice()!;
    expect(notice.body).toBe("Continuing 2 sessions.");
    expect(notice.actions?.map((a) => a.label)).toEqual(["Stop all"]);
    expect(notice.link).toBeUndefined();

    await notice.actions![0]!.run();
    expect(postMock).toHaveBeenCalledWith("/api/sessions/interruptions/stop-automatic");
    expect(postMock).toHaveBeenCalledTimes(1);
  });

  it("Esc on the automatic notice stops the sends too", async () => {
    useSessionsStore().setSessions([item("a", "One", false, true, true)]);
    mount(Host, { global: { plugins: [pinia] } });
    await nextTick();

    restartNotice()!.onEscape!();
    expect(postMock).toHaveBeenCalledWith("/api/sessions/interruptions/stop-automatic");
  });

  it("keeps saying how many it is continuing while the sends go out", async () => {
    const store = useSessionsStore();
    store.setSessions([item("a", "One", false, true, true), item("b", "Two", false, true, true)]);
    mount(Host, { global: { plugins: [pinia] } });
    await nextTick();

    store.setSessions([item("a", "One", false, false), item("b", "Two", false, true, true)]);
    await nextTick();
    expect(restartNotice()!.body).toBe("Continuing 2 sessions.");
  });

  it("says one line per other machine that restarted", () => {
    const entries = [
      { key: "home", name: "Home", isHome: true, isLive: true },
      { key: "m1", name: "build-box", isHome: false, isLive: false },
      { key: "m2", name: "laptop", isHome: false, isLive: false },
      { key: "m3", name: "quiet", isHome: false, isLive: false },
    ] as MachineEntry[];
    const sessions = (items: SessionListItem[]) => ({ sessions: items, projects: [], error: null, loadedAt: 1, loading: false });
    const lines = otherMachineLines(entries, {
      m1: sessions([item("x", "Far one")]),
      m2: sessions([item("y", "A"), item("z", "B")]),
      m3: sessions([item("w", "Idle", false, false)]),
    });
    expect(lines).toEqual(["build-box restarted: 1 session cut off", "laptop restarted: 2 sessions cut off"]);
  });
});
