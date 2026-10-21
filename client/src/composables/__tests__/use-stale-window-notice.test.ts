import { enableAutoUnmount, mount } from "@vue/test-utils";
import { createPinia, setActivePinia } from "pinia";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { defineComponent, nextTick, shallowRef } from "vue";
import type { MachineEntry } from "@/stores/machines";

const { entries, loadHome, updatesOn } = vi.hoisted(() => ({ entries: { value: null as unknown }, loadHome: vi.fn(), updatesOn: { value: null as unknown } }));

vi.mock("@tanstack/vue-router", () => ({ useRouter: () => ({ navigate: vi.fn() }) }));
vi.mock("@/composables/use-whats-new", () => ({ useWhatsNew: () => ({ openWhatsNew: vi.fn() }) }));
vi.mock("@/composables/use-fleet-updates", async () => {
  const { shallowRef: ref } = await import("vue");
  updatesOn.value = ref(false);
  return { useFleetUpdatesWatch: () => ({ enabled: updatesOn.value, waiting: ref(0), rollout: ref(null) }) };
});
vi.mock("@/stores/machines", async () => {
  const { shallowRef: ref } = await import("vue");
  entries.value = ref<MachineEntry[]>([]);
  return { useMachinesStore: () => ({ loadHome, get entries() { return (entries.value as { value: MachineEntry[] }).value; } }) };
});

const { useStaleWindowNotice } = await import("@/composables/use-stale-window-notice");
const { useNoticesStore } = await import("@/stores/notices");
const { useFleetUpdateNotices } = await import("@/composables/use-fleet-update-notices");

enableAutoUnmount(afterEach);
const entriesRef = () => entries.value as ReturnType<typeof shallowRef<MachineEntry[]>>;
const home = (version: string | null) => [{ key: "home", isHome: true, info: version ? { version } : null }] as unknown as MachineEntry[];

describe("useStaleWindowNotice", () => {
  beforeEach(() => {
    setActivePinia(createPinia());
    loadHome.mockClear();
    entriesRef().value = [];
  });

  it("says nothing while the Fleet stays the version this window loaded against", async () => {
    entriesRef().value = home("0.52.0");
    mount(defineComponent({ setup() { useStaleWindowNotice(); return () => null; } }));
    entriesRef().value = home("0.52.0");
    await nextTick();
    expect(useNoticesStore().notices).toHaveLength(0);
  });

  it("offers a reload once the Fleet it was loaded from has become another version", async () => {
    entriesRef().value = home("0.52.0");
    mount(defineComponent({ setup() { useStaleWindowNotice(); return () => null; } }));
    entriesRef().value = home("0.53.0");
    await nextTick();
    const notice = useNoticesStore().notices.find((candidate) => candidate.id === "fleet-window-stale:0.53.0");
    expect(notice?.title).toBe("Fleet updated to 0.53.0");
    expect(notice?.actions?.map((action) => action.label)).toEqual(["Reload", "Later"]);
  });

  it("takes the first version it sees only when the machine has been read", async () => {
    mount(defineComponent({ setup() { useStaleWindowNotice(); return () => null; } }));
    entriesRef().value = home(null);
    await nextTick();
    entriesRef().value = home("0.52.0");
    await nextTick();
    expect(useNoticesStore().notices).toHaveLength(0);
  });

  it("reads the Fleet again when the window comes back to the front, and every minute while it shows", () => {
    vi.useFakeTimers();
    mount(defineComponent({ setup() { useStaleWindowNotice(); return () => null; } }));
    expect(loadHome).toHaveBeenCalledTimes(1);
    vi.advanceTimersByTime(60_000);
    expect(loadHome).toHaveBeenCalledTimes(2);
    document.dispatchEvent(new Event("visibilitychange"));
    expect(loadHome).toHaveBeenCalledTimes(3);
    vi.useRealTimers();
  });

  it("with the Updates switch on, says it once: the updated notice carries Reload and this posts none", async () => {
    (updatesOn.value as ReturnType<typeof shallowRef<boolean>>).value = true;
    const withUpdate = (version: string) => [{
      key: "home", name: "falcon", isHome: true,
      info: { version, update: { status: "uptodate", latestVersion: version, stagedVersion: null, checkedAt: null, lastUpdate: { from: "0.52.0", to: version, at: new Date().toISOString(), trigger: "idle" } } },
    }] as unknown as MachineEntry[];
    entriesRef().value = [{ key: "home", name: "falcon", isHome: true, info: { version: "0.52.0" } }] as unknown as MachineEntry[];
    mount(defineComponent({ setup() { useFleetUpdateNotices(); useStaleWindowNotice(); return () => null; } }));
    const notices = useNoticesStore();
    entriesRef().value = withUpdate("0.53.0");
    await nextTick();

    const about = notices.notices.filter((notice) => notice.id.startsWith("fleet-updated:") || notice.id.startsWith("fleet-window-stale:"));
    expect(about.map((notice) => notice.title)).toEqual(["falcon updated to 0.53.0"]);
    expect(about[0]!.actions?.map((action) => action.label)).toEqual(["Reload", "What's new", "Dismiss"]);
    (updatesOn.value as ReturnType<typeof shallowRef<boolean>>).value = false;
  });
});
