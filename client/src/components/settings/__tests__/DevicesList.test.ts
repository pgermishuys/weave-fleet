import { flushPromises, mount } from "@vue/test-utils";
import { beforeEach, describe, expect, it, vi } from "vitest";
import DevicesList from "@/components/settings/DevicesList.vue";
import type { PairedDevice } from "@/lib/devices-api";

const { confirmAction, removeDevice, removeFleetScreen } = vi.hoisted(() => ({
  confirmAction: vi.fn(async () => true),
  removeDevice: vi.fn(async () => {}),
  removeFleetScreen: vi.fn(async () => {}),
}));
vi.mock("@/components/ui/confirm", () => ({ confirmAction }));
vi.mock("@/lib/devices-api", () => ({ removeDevice, removeFleetScreen }));

const phone: PairedDevice = {
  id: "d1",
  name: "Robin's phone",
  platform: "ios",
  createdAt: "2026-11-01T09:00:00.000Z",
  lastUsedAt: "2026-11-14T09:00:00.000Z",
  pairedVia: null,
  kind: "device",
};
const studio: PairedDevice = { ...phone, id: "m1", name: "studio", platform: "linux", kind: "machine" };

function mountList() {
  return mount(DevicesList, { props: { devices: [phone, studio], thisComputer: "Signed in with the master token" } });
}

describe("DevicesList: Remove", () => {
  beforeEach(() => {
    confirmAction.mockClear();
    removeDevice.mockClear();
  });

  it("asks first, saying what the device loses, then removes it", async () => {
    confirmAction.mockResolvedValueOnce(true);
    const wrapper = mountList();

    await wrapper.get("[data-testid='remove-device-d1']").trigger("click");
    await flushPromises();

    expect(confirmAction).toHaveBeenCalledWith(expect.objectContaining({
      title: "Remove Robin's phone?",
      body: "It loses access to this machine at once. Other devices aren't affected.",
      confirmLabel: "Remove device",
      destructive: true,
    }));
    expect(removeDevice).toHaveBeenCalledWith("d1");
    expect(wrapper.emitted("removed")).toEqual([["d1"]]);
  });

  it("says it reaches every machine when Your Fleet is on, for a phone but not for a machine's key", async () => {
    confirmAction.mockResolvedValue(false);
    const wrapper = mount(DevicesList, { props: { devices: [phone, studio], thisComputer: "x", everywhere: true } });

    await wrapper.get("[data-testid='remove-device-d1']").trigger("click");
    await flushPromises();
    expect(confirmAction).toHaveBeenLastCalledWith(expect.objectContaining({
      title: "Remove Robin's phone on every machine in your Fleet?",
      confirmLabel: "Remove Robin's phone",
      destructive: true,
    }));

    await wrapper.get("[data-testid='remove-device-m1']").trigger("click");
    await flushPromises();
    expect(confirmAction).toHaveBeenLastCalledWith(expect.objectContaining({ confirmLabel: "Remove machine" }));
    confirmAction.mockResolvedValue(true);
  });

  it("names a paired machine as a machine", async () => {
    confirmAction.mockResolvedValueOnce(false);
    const wrapper = mountList();

    await wrapper.get("[data-testid='remove-device-m1']").trigger("click");
    await flushPromises();

    expect(confirmAction).toHaveBeenCalledWith(expect.objectContaining({ confirmLabel: "Remove machine" }));
  });

  it("removes nothing when the confirm is cancelled", async () => {
    confirmAction.mockResolvedValueOnce(false);
    const wrapper = mountList();

    await wrapper.get("[data-testid='remove-device-d1']").trigger("click");
    await flushPromises();

    expect(removeDevice).not.toHaveBeenCalled();
    expect(wrapper.emitted("removed")).toBeUndefined();
  });

  it("lists a phone of the whole Fleet with the machine it connected through, and removes it by its Fleet-wide id", async () => {
    confirmAction.mockResolvedValueOnce(true);
    const iphone: PairedDevice = { ...phone, id: "screen:abc", name: "Alex's iPhone", throughName: "atlas", fleetWide: true };
    const wrapper = mount(DevicesList, { props: { devices: [iphone], thisComputer: "x", everywhere: true, hideThisMachine: true } });

    expect(wrapper.get("[data-testid='device-row']").text()).toContain("Connected through atlas, added");

    await wrapper.get("[data-testid='remove-device-screen:abc']").trigger("click");
    await flushPromises();

    expect(removeFleetScreen).toHaveBeenCalledWith("screen:abc");
    expect(removeDevice).not.toHaveBeenCalled();
    expect(wrapper.emitted("removed")).toEqual([["screen:abc"]]);
  });

  it("says which machines aren't answering", () => {
    const one = mount(DevicesList, { props: { devices: [], thisComputer: "x", notAnswering: ["atlas"] } });
    expect(one.get("[data-testid='devices-not-answering']").text()).toBe("atlas isn't answering, so what it holds isn't shown.");
    const two = mount(DevicesList, { props: { devices: [], thisComputer: "x", notAnswering: ["atlas", "workbench"] } });
    expect(two.get("[data-testid='devices-not-answering']").text()).toBe("atlas, workbench aren't answering, so what they hold isn't shown.");
    expect(mount(DevicesList, { props: { devices: [], thisComputer: "x" } }).find("[data-testid='devices-not-answering']").exists()).toBe(false);
  });
});
