import { enableAutoUnmount, mount } from "@vue/test-utils";
import { createPinia, setActivePinia } from "pinia";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { defineComponent, nextTick, shallowRef } from "vue";
import type { MachineEntry } from "@/stores/machines";

const { entries, enabled, waiting, rollout, openWhatsNew } = vi.hoisted(() => ({
  rollout: { value: null as unknown },
  entries: { value: null as unknown },
  enabled: { value: null as unknown },
  waiting: { value: null as unknown },
  openWhatsNew: vi.fn(),
}));

vi.mock("@tanstack/vue-router", () => ({ useRouter: () => ({ navigate: vi.fn() }) }));
vi.mock("@/composables/use-whats-new", () => ({ useWhatsNew: () => ({ openWhatsNew }) }));
vi.mock("@/composables/use-fleet-updates", async () => {
  const { shallowRef: ref } = await import("vue");
  enabled.value = ref(true);
  waiting.value = ref(0);
  rollout.value = ref(null);
  return { useFleetUpdatesWatch: () => ({ enabled: enabled.value, waiting: waiting.value, rollout: rollout.value }) };
});
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
    (enabled.value as ReturnType<typeof shallowRef<boolean>>).value = true;
    openWhatsNew.mockClear();
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

  it("keeps a chip that counts the Fleets that can update, and takes it away at zero or with the switch off", async () => {
    mountIt();
    const notices = useNoticesStore();
    expect(notices.chips).toHaveLength(0);

    waitingRef().value = 2;
    await nextTick();
    expect(notices.chips.map((chip) => chip.chip)).toEqual(["↑ 2 machines can update"]);

    waitingRef().value = 1;
    await nextTick();
    expect(notices.chips.map((chip) => chip.chip)).toEqual(["↑ 1 machine can update"]);

    (enabled.value as ReturnType<typeof shallowRef<boolean>>).value = false;
    await nextTick();
    expect(notices.chips).toHaveLength(0);
  });

  it("turns the one chip into the rollout's progress while machines update, and a click opens Settings → Machines", async () => {
    const { useSettingsNav } = await import("@/composables/use-settings-nav");
    mountIt();
    const notices = useNoticesStore();
    waitingRef().value = 3;
    rolloutRef().value = { id: "r", status: "running", startedAt: "", steps: [{ state: "done" }, { state: "waiting" }, { state: "pending" }] };
    await nextTick();
    expect(notices.chips.map((chip) => chip.chip)).toEqual(["Updating machines · 2 of 3"]);

    notices.chips[0]!.onChipClick?.();
    expect(useSettingsNav().activeSection.value).toBe("machines");
  });
});
