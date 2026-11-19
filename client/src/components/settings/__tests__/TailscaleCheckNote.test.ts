import { flushPromises, mount } from "@vue/test-utils";
import { beforeEach, describe, expect, it, vi } from "vitest";
import TailscaleCheckNote from "@/components/settings/TailscaleCheckNote.vue";
import { useMachineReach } from "@/composables/use-machine-reach";
import type { ReachState, TailscaleCheck } from "@/lib/reach-api";

const api = vi.hoisted(() => ({ loadReach: vi.fn(), saveReach: vi.fn() }));
vi.mock("@/lib/reach-api", () => api);

const state = (tailscale: TailscaleCheck): ReachState => ({ available: true, mode: "tailscale-and-home", ownAddress: null, ownPort: 6263, listening: [], found: [], tailscale });

const notListening: TailscaleCheck = { state: "running", headline: "Fleet isn't listening on Tailscale yet", detail: "Turn it on.", covered: false, canTurnOn: true };
const off: TailscaleCheck = { state: "stopped", headline: "Tailscale is switched off on this machine", detail: "Switch it on.", covered: false, canTurnOn: false };

describe("Tailscale check on this machine", () => {
  beforeEach(() => {
    api.loadReach.mockReset();
    api.saveReach.mockReset();
    useMachineReach().state.value = null;
  });

  it("offers Turn on for the listener when Tailscale itself is fine, and opens it for Tailscale and home", async () => {
    api.saveReach.mockResolvedValue(state({ state: "running", headline: "Tailscale is on", detail: "", covered: true, canTurnOn: false }));
    const wrapper = mount(TailscaleCheckNote, { props: { check: notListening } });

    expect(wrapper.text()).toContain("Fleet isn't listening on Tailscale yet");
    await wrapper.get("[data-testid='tailscale-turn-on']").trigger("click");
    await flushPromises();

    expect(api.saveReach).toHaveBeenCalledWith("tailscale-and-home", undefined);
    expect(wrapper.emitted("changed")).toHaveLength(1);
  });

  it("says what's wrong and offers Check again, not Turn on, when Tailscale is off", async () => {
    api.loadReach.mockResolvedValue(state(off));
    const wrapper = mount(TailscaleCheckNote, { props: { check: off } });

    expect(wrapper.find("[data-testid='tailscale-turn-on']").exists()).toBe(false);
    await wrapper.get("[data-testid='tailscale-check-again']").trigger("click");
    await flushPromises();

    expect(api.loadReach).toHaveBeenCalled();
  });

  it("shows the failure when Turn on can't open the listener", async () => {
    api.saveReach.mockRejectedValue(new Error("Fleet couldn't listen on 100.64.0.2:6262."));
    const wrapper = mount(TailscaleCheckNote, { props: { check: notListening } });

    await wrapper.get("[data-testid='tailscale-turn-on']").trigger("click");
    await flushPromises();

    expect(wrapper.text()).toContain("Fleet couldn't listen on 100.64.0.2:6262.");
  });
});
