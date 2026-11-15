import { flushPromises, mount } from "@vue/test-utils";
import { beforeEach, describe, expect, it, vi } from "vitest";
import DevicesList from "@/components/settings/DevicesList.vue";
import type { PairedDevice } from "@/lib/devices-api";

const { confirmAction, removeDevice } = vi.hoisted(() => ({
  confirmAction: vi.fn(async () => true),
  removeDevice: vi.fn(async () => {}),
}));
vi.mock("@/components/ui/confirm", () => ({ confirmAction }));
vi.mock("@/lib/devices-api", () => ({ removeDevice }));

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
});
