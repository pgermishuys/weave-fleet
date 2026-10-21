import { enableAutoUnmount, mount } from "@vue/test-utils";
import { createPinia, setActivePinia } from "pinia";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { defineComponent, nextTick, shallowRef } from "vue";
import type { MachineEntry } from "@/stores/machines";

const { entries } = vi.hoisted(() => ({ entries: { value: null as unknown } }));

vi.mock("@/stores/machines", async () => {
  const { shallowRef: ref } = await import("vue");
  entries.value = ref<MachineEntry[]>([]);
  return { useMachinesStore: () => ({ get entries() { return (entries.value as { value: MachineEntry[] }).value; } }) };
});

const { useStaleWindowNotice } = await import("@/composables/use-stale-window-notice");
const { useNoticesStore } = await import("@/stores/notices");

enableAutoUnmount(afterEach);
const entriesRef = () => entries.value as ReturnType<typeof shallowRef<MachineEntry[]>>;
const home = (version: string | null) => [{ key: "home", isHome: true, info: version ? { version } : null }] as unknown as MachineEntry[];

describe("useStaleWindowNotice", () => {
  beforeEach(() => {
    setActivePinia(createPinia());
    sessionStorage.clear();
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
});
