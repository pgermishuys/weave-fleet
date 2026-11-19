import { shallowRef } from "vue";
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { PairedDevice } from "@/lib/devices-api";

const { listDevices, listFleetScreens } = vi.hoisted(() => ({ listDevices: vi.fn(), listFleetScreens: vi.fn() }));
vi.mock("@/lib/devices-api", () => ({ listDevices, listFleetScreens }));
vi.mock("@/stores/machines", () => ({ useMachinesStore: () => ({ loadHomeAccess: vi.fn() }) }));
vi.mock("@/composables/use-machine-reach", () => ({ useMachineReach: () => ({ load: vi.fn() }) }));

import { useHomeAccess } from "@/composables/use-home-access";

const own: PairedDevice = { id: "d1", name: "This machine's phone", platform: "ios", createdAt: "2026-11-01T09:00:00Z", lastUsedAt: "2026-11-02T09:00:00Z", pairedVia: null, kind: "device" };
const fleetWide: PairedDevice = { ...own, id: "screen:abc", name: "Alex's iPhone", throughName: "atlas", fleetWide: true };
const studio: PairedDevice = { ...own, id: "m1", kind: "machine" };

describe("useHomeAccess: the phones and browsers", () => {
  beforeEach(() => {
    listDevices.mockReset().mockResolvedValue([own, studio]);
    listFleetScreens.mockReset();
  });

  it("lists only this machine's devices with Your Fleet off, and never asks the others", async () => {
    const access = useHomeAccess(shallowRef(false));
    await access.refreshDevices();

    expect(listFleetScreens).not.toHaveBeenCalled();
    expect(access.screens.value.map((device) => device.id)).toEqual(["d1"]);
    expect(access.notAnswering.value).toEqual([]);
  });

  it("lists the whole Fleet with Your Fleet on, and keeps the machines that didn't answer", async () => {
    listFleetScreens.mockResolvedValue({ screens: [fleetWide], notAnswering: [{ id: "m2", name: "workbench" }] });
    const access = useHomeAccess(shallowRef(true));
    await access.refreshDevices();

    expect(access.screens.value).toEqual([fleetWide]);
    expect(access.notAnswering.value).toEqual([{ id: "m2", name: "workbench" }]);
    expect(listDevices).not.toHaveBeenCalled();
  });

  it("falls back to this machine's own list when the Fleet-wide list can't be had", async () => {
    listFleetScreens.mockRejectedValue(new Error("Your Fleet is off on this machine."));
    const access = useHomeAccess(shallowRef(true));
    await access.refreshDevices();

    expect(access.screens.value.map((device) => device.id)).toEqual(["d1"]);
    expect(access.notAnswering.value).toEqual([]);
  });
});
