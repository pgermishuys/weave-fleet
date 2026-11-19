import { flushPromises, mount } from "@vue/test-utils";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import ReachDialog from "@/components/settings/ReachDialog.vue";
import { useMachineReach } from "@/composables/use-machine-reach";
import type { ReachState } from "@/lib/reach-api";

const api = vi.hoisted(() => ({ loadReach: vi.fn(), saveReach: vi.fn() }));
vi.mock("@/lib/reach-api", () => api);

const off: ReachState = {
  available: true,
  mode: "off",
  ownAddress: null,
  ownPort: 6263,
  listening: [],
  found: [
    { url: "http://studio.tail4f2a.ts.net:6262", kind: "tailnet" },
    { url: "http://192.168.1.20:6262", kind: "lan" },
  ],
};

function mountDialog() {
  return mount(ReachDialog, { props: { open: true, machineName: "studio" }, attachTo: document.body, global: { stubs: { teleport: false } } });
}

const byTestId = (id: string) => document.body.querySelector<HTMLElement>(`[data-testid='${id}']`);

describe("Let others reach studio?", () => {
  beforeEach(() => {
    api.loadReach.mockReset().mockResolvedValue(off);
    api.saveReach.mockReset();
    useMachineReach().state.value = null;
  });
  afterEach(() => {
    document.body.innerHTML = "";
  });

  it("asks the question and shows the three choices with the addresses Fleet found", async () => {
    mountDialog();
    await flushPromises();

    expect(document.body.textContent).toContain("Let others reach studio?");
    expect(document.body.textContent).toContain("Tailscale and home network");
    expect(document.body.textContent).toContain("Home network only");
    expect(document.body.textContent).toContain("My own address");
    expect(byTestId("reach-address-tailnet")?.textContent).toContain("studio.tail4f2a.ts.net");
    expect(byTestId("reach-address-lan")?.textContent).toContain("192.168.1.20");
    expect(byTestId("reach-choice-tailscale-and-home")?.hasAttribute("checked") || (byTestId("reach-choice-tailscale-and-home") as HTMLInputElement).checked).toBe(true);
  });

  it("turns it on with the picked choice and tells the parent", async () => {
    api.saveReach.mockResolvedValue({ ...off, mode: "home" });
    const wrapper = mountDialog();
    await flushPromises();

    (byTestId("reach-choice-home") as HTMLInputElement).click();
    await flushPromises();
    byTestId("reach-turn-on")!.click();
    await flushPromises();

    expect(api.saveReach).toHaveBeenCalledWith("home", undefined);
    expect(wrapper.emitted("changed")).toHaveLength(1);
    expect(wrapper.emitted("update:open")?.at(-1)).toEqual([false]);
  });

  it("needs an address for my own address, and sends it", async () => {
    api.saveReach.mockResolvedValue({ ...off, mode: "own", ownAddress: "https://fleet.example.com" });
    mountDialog();
    await flushPromises();

    (byTestId("reach-choice-own") as HTMLInputElement).click();
    await flushPromises();
    expect((byTestId("reach-turn-on") as HTMLButtonElement).disabled).toBe(true);
    expect(document.body.textContent).toContain("127.0.0.1:6263");

    const field = byTestId("reach-own-address") as HTMLInputElement;
    field.value = "https://fleet.example.com";
    field.dispatchEvent(new Event("input"));
    await flushPromises();
    byTestId("reach-turn-on")!.click();
    await flushPromises();

    expect(api.saveReach).toHaveBeenCalledWith("own", "https://fleet.example.com");
  });

  it("shows what Fleet said when it can't listen, and stays open", async () => {
    api.saveReach.mockRejectedValue(new Error("Fleet couldn't listen on 192.168.1.20:6262. Something else may be using the port."));
    const wrapper = mountDialog();
    await flushPromises();

    byTestId("reach-turn-on")!.click();
    await flushPromises();

    expect(document.body.textContent).toContain("Something else may be using the port");
    expect(wrapper.emitted("changed")).toBeUndefined();
  });

  it("offers to turn it off again once it is on", async () => {
    api.loadReach.mockResolvedValue({ ...off, mode: "tailscale-and-home" });
    api.saveReach.mockResolvedValue(off);
    mountDialog();
    await flushPromises();

    byTestId("reach-turn-off")!.click();
    await flushPromises();

    expect(api.saveReach).toHaveBeenCalledWith("off", undefined);
  });
});
