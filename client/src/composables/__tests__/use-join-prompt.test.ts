import { enableAutoUnmount, flushPromises, mount } from "@vue/test-utils";
import { createPinia, setActivePinia } from "pinia";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { defineComponent, shallowRef } from "vue";
import type { FoundMachine } from "@/stores/machines";

const navigate = vi.hoisted(() => vi.fn());
vi.mock("@tanstack/vue-router", () => ({ useRouter: () => ({ navigate }) }));

const state = vi.hoisted(() => ({
  found: null as unknown,
  loadFound: vi.fn(),
}));
vi.mock("@/stores/machines", async () => {
  const { shallowRef: ref } = await import("vue");
  state.found = ref<FoundMachine[] | null>(null);
  return { useMachinesStore: () => ({ get foundMachines() { return (state.found as { value: FoundMachine[] | null }).value; }, loadFound: state.loadFound }) };
});

const { useJoinPrompt, joinNoticeId, JOIN_LOOK_EVERY_MS } = await import("@/composables/use-join-prompt");
const { useNoticesStore } = await import("@/stores/notices");
const { useSettingsNav } = await import("@/composables/use-settings-nav");

enableAutoUnmount(afterEach);

const foundRef = () => state.found as ReturnType<typeof shallowRef<FoundMachine[] | null>>;
const atlas: FoundMachine = { machineId: "m-atlas", name: "atlas", os: "linux", baseUrl: "http://192.168.1.40:6262", hint: "7K3M", source: "Home network", expiresAt: "2026-11-19T18:00:00Z" };

function mountIt() {
  return mount(defineComponent({ setup() { useJoinPrompt(); return () => null; } }));
}

describe("useJoinPrompt", () => {
  beforeEach(() => {
    vi.useFakeTimers();
    localStorage.clear();
    setActivePinia(createPinia());
    navigate.mockClear();
    foundRef().value = [];
    state.loadFound.mockReset().mockResolvedValue(undefined);
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it("says a waiting machine wants to join, and takes you to Add a machine", async () => {
    foundRef().value = [atlas];
    mountIt();
    await flushPromises();

    const notices = useNoticesStore();
    const notice = notices.notices.find((n) => n.id === joinNoticeId("m-atlas"))!;
    expect(notice.title).toBe("atlas wants to join your Fleet");
    expect(notice.chip).toBe("atlas wants to join");

    await notice.actions!.find((a) => a.label === "Add atlas")!.run();
    expect(useSettingsNav().addMachineRequested.value).toBe(true);
    expect(navigate).toHaveBeenCalledWith({ to: "/settings" });
  });

  it("takes the notice away when the machine stops waiting, and looks again every so often", async () => {
    foundRef().value = [atlas];
    mountIt();
    await flushPromises();
    const notices = useNoticesStore();
    expect(notices.has(joinNoticeId("m-atlas"))).toBe(true);

    foundRef().value = [];
    await vi.advanceTimersByTimeAsync(JOIN_LOOK_EVERY_MS + 10);

    expect(state.loadFound).toHaveBeenCalledTimes(2);
    expect(notices.has(joinNoticeId("m-atlas"))).toBe(false);
  });
});
