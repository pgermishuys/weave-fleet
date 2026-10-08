import { defineComponent, h } from "vue";
import { mount } from "@vue/test-utils";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

const handlers: { disconnect?: () => void; reconnect?: () => void } = {};
vi.mock("@/composables/use-weave-socket", () => ({
  onDisconnect: (_machine: unknown, cb: () => void) => { handlers.disconnect = cb; return () => undefined; },
  onConnectionLost: () => () => undefined,
  onReconnect: (_machine: unknown, cb: () => void) => { handlers.reconnect = cb; return () => undefined; },
}));
const fetchOnMachine = vi.fn();
vi.mock("@/lib/machines", () => ({ fetchOnMachine: (...args: unknown[]) => fetchOnMachine(...args) }));
vi.mock("@/lib/machine-target", () => ({ useMachineTarget: () => ({ key: "home", connection: null, isLive: true, api: {} }) }));

import { RETRY_STEPS, useMachineReachability } from "../phone/use-machine-reachability";

describe("useMachineReachability", () => {
  beforeEach(() => {
    vi.useFakeTimers();
    fetchOnMachine.mockReset();
  });
  afterEach(() => vi.useRealTimers());

  function setup(onBack = vi.fn()) {
    let api!: ReturnType<typeof useMachineReachability>;
    mount(defineComponent({ setup: () => { api = useMachineReachability(onBack); return () => h("div"); } }));
    return { api, onBack };
  }

  it("goes away when the hub drops, counts down, and comes back when the machine answers", async () => {
    const { api, onBack } = setup();
    fetchOnMachine.mockRejectedValueOnce(new TypeError("offline")).mockResolvedValueOnce(new Response("{}", { status: 200 }));

    handlers.disconnect?.();
    expect(api.reachable.value).toBe(false);
    expect(api.retryIn.value).toBe(RETRY_STEPS[0]);

    await vi.advanceTimersByTimeAsync(RETRY_STEPS[0] * 1000);
    expect(api.reachable.value).toBe(false);
    expect(api.retryIn.value).toBe(RETRY_STEPS[1]);

    await vi.advanceTimersByTimeAsync(RETRY_STEPS[1] * 1000);
    expect(api.reachable.value).toBe(true);
    expect(onBack).toHaveBeenCalledTimes(1);
  });

  it("comes back when the hub reconnects", () => {
    const { api, onBack } = setup();
    handlers.disconnect?.();
    handlers.reconnect?.();
    expect(api.reachable.value).toBe(true);
    expect(onBack).toHaveBeenCalledTimes(1);
  });

  it("keeps the last heard time while away", () => {
    const { api } = setup();
    api.heard();
    const heard = api.lastHeardAt.value;
    handlers.disconnect?.();
    vi.advanceTimersByTime(1000);
    api.heard();
    expect(api.lastHeardAt.value).toBe(heard);
  });
});
