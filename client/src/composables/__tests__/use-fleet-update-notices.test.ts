import { enableAutoUnmount, mount } from "@vue/test-utils";
import { createPinia, setActivePinia } from "pinia";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { defineComponent, nextTick, shallowRef } from "vue";
import type { MachineEntry } from "@/stores/machines";

const { entries, waiting, rollout, openWhatsNew } = vi.hoisted(() => ({
  rollout: { value: null as unknown },
  entries: { value: null as unknown },
  waiting: { value: null as unknown },
  openWhatsNew: vi.fn(),
}));

vi.mock("@tanstack/vue-router", () => ({ useRouter: () => ({ navigate: vi.fn() }) }));
vi.mock("@/composables/use-whats-new", () => ({ useWhatsNew: () => ({ openWhatsNew }) }));
vi.mock("@/composables/use-fleet-updates", async () => {
  const { shallowRef: ref } = await import("vue");
  waiting.value = ref(0);
  rollout.value = ref(null);
  return { useFleetUpdatesWatch: () => ({ waiting: waiting.value, rollout: rollout.value }) };
});
const prefs = vi.hoisted(() => ({ values: {} as Record<string, string>, fetched: true, set: vi.fn() }));
vi.mock("@/stores/preferences", () => ({
  usePreferencesStore: () => ({
    ensureLoaded: vi.fn(),
    get hasFetched() { return prefs.fetched; },
    get: (key: string, fallback: string) => prefs.values[key] ?? fallback,
    set: prefs.set.mockImplementation(async (key: string, value: string) => { prefs.values[key] = value; }),
  }),
}));
vi.mock("@/stores/machines", async () => {
  const { shallowRef: ref } = await import("vue");
  entries.value = ref<MachineEntry[]>([]);
  return { useMachinesStore: () => ({ get entries() { return (entries.value as { value: MachineEntry[] }).value; } }) };
});

const { useFleetUpdateNotices } = await import("@/composables/use-fleet-update-notices");
const { useNoticesStore } = await import("@/stores/notices");

enableAutoUnmount(afterEach);

const entriesRef = () => entries.value as ReturnType<typeof shallowRef<MachineEntry[]>>;
const rolloutRef = () => rollout.value as ReturnType<typeof shallowRef<unknown>>;
const waitingRef = () => waiting.value as ReturnType<typeof shallowRef<number>>;

function falcon(version: string, lastUpdate: unknown): MachineEntry {
  return {
    key: "falcon-id", name: "falcon", os: "linux", baseUrl: "", isHome: false, isLive: false, connection: null, capabilities: null,
    info: { version, update: { status: "uptodate", latestVersion: version, stagedVersion: null, checkedAt: null, lastUpdate } },
  } as unknown as MachineEntry;
}

function mountIt() {
  return mount(defineComponent({ setup() { useFleetUpdateNotices(); return () => null; } }));
}

describe("useFleetUpdateNotices", () => {
  beforeEach(() => {
    localStorage.clear();
    setActivePinia(createPinia());
    entriesRef().value = [];
    waitingRef().value = 0;
    rolloutRef().value = null;
    openWhatsNew.mockClear();
    prefs.values = {};
    prefs.fetched = true;
    prefs.set.mockClear();
    vi.useFakeTimers({ now: new Date("2026-10-20T12:00:00Z") });
  });
  afterEach(() => vi.useRealTimers());

  it("announces a machine's update once per machine and version, not again when the machine is read again or the page reloads", async () => {
    const update = { from: "0.59.0", to: "0.59.1", at: "2026-10-20T03:12:00Z", trigger: "idle" };
    entriesRef().value = [falcon("0.59.1", update)];
    mountIt();
    const notices = useNoticesStore();
    expect(notices.notices.filter((notice) => notice.title === "falcon updated to 0.59.1")).toHaveLength(1);

    notices.openNext();
    notices.settle();
    entriesRef().value = [falcon("0.59.1", update)];
    await nextTick();
    expect(notices.notices.filter((notice) => notice.id.startsWith("fleet-updated:"))).toHaveLength(1);
    expect(notices.nextWaiting).toBeNull();

    // After a reload the store is empty but the browser remembers it showed this one.
    setActivePinia(createPinia());
    mountIt();
    const afterReload = useNoticesStore();
    expect(afterReload.nextWaiting).toBeNull();
    expect(afterReload.open).toBeNull();
  });

  it("keeps a chip that counts the Fleets that can update, and takes it away at zero", async () => {
    mountIt();
    const notices = useNoticesStore();
    expect(notices.chips).toHaveLength(0);

    waitingRef().value = 2;
    await nextTick();
    expect(notices.chips.map((chip) => chip.chip)).toEqual(["↑ 2 machines can update"]);

    waitingRef().value = 1;
    await nextTick();
    expect(notices.chips.map((chip) => chip.chip)).toEqual(["↑ 1 machine can update"]);

    waitingRef().value = 0;
    await nextTick();
    expect(notices.chips).toHaveLength(0);
  });

  it("turns the one chip into the rollout's progress while machines update, and a click opens Settings → Updates & about › Machines", async () => {
    const { useSettingsNav } = await import("@/composables/use-settings-nav");
    mountIt();
    const notices = useNoticesStore();
    waitingRef().value = 3;
    rolloutRef().value = { id: "r", status: "running", startedAt: "", steps: [{ state: "done" }, { state: "waiting" }, { state: "pending" }] };
    await nextTick();
    expect(notices.chips.map((chip) => chip.chip)).toEqual(["Updating machines · 2 of 3"]);

    notices.chips[0]!.onChipClick?.();
    expect(useSettingsNav().activeSection.value).toBe("updates");
    expect(useSettingsNav().activeAnchor.value).toBe("machines");
  });

  describe("a machine that restarted itself under a rule nobody picked", () => {
    const update = { from: "0.59.0", to: "0.59.1", at: "2026-10-20T03:12:00Z", trigger: "idle" };
    const updated = () => useNoticesStore().notices.find((notice) => notice.id.startsWith("fleet-updated:"));

    it("tells the user once, with a way to change the rule, and never again", async () => {
      entriesRef().value = [falcon("0.59.1", update)];
      mountIt();
      expect(updated()?.body).toContain("Nobody chose an update rule");
      expect(updated()?.actions?.map((action) => action.label)).toEqual(["What's new", "Change the rule", "Dismiss"]);
      expect(prefs.set).toHaveBeenCalledWith("FleetUpdateRuleNotice", "true");

      // The next self-update, on another machine or version, is not announced that way.
      useNoticesStore().remove(updated()!.id);
      entriesRef().value = [falcon("0.59.2", { ...update, from: "0.59.1", to: "0.59.2" })];
      await nextTick();
      expect(updated()?.body).not.toContain("Nobody chose");
    });

    it("says nothing about the rule when the user chose one, or the update was theirs, or preferences haven't loaded", async () => {
      prefs.values = { FleetUpdateRules: JSON.stringify({ "falcon-id": "idle" }) };
      entriesRef().value = [falcon("0.59.1", update)];
      mountIt();
      expect(updated()?.body).not.toContain("Nobody chose");
      expect(prefs.set).not.toHaveBeenCalled();

      setActivePinia(createPinia());
      prefs.values = {};
      entriesRef().value = [falcon("0.59.2", { ...update, to: "0.59.2", trigger: "manual" })];
      mountIt();
      expect(prefs.set).not.toHaveBeenCalled();

      setActivePinia(createPinia());
      prefs.fetched = false;
      entriesRef().value = [falcon("0.59.3", { ...update, to: "0.59.3" })];
      mountIt();
      expect(prefs.set).not.toHaveBeenCalled();
    });
  });
});
