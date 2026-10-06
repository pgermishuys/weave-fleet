import "fake-indexeddb/auto";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { clearCredentials, readCredentials } from "@/lib/device-credentials";
import { restoreCredentials } from "../restore-credentials";

function reply(status: number, body: unknown): typeof fetch {
  return vi.fn(async () => new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } })) as unknown as typeof fetch;
}

describe("restoring a signed-in phone's credentials", () => {
  beforeEach(async () => {
    await clearCredentials();
    localStorage.clear();
  });

  it("saves the new token and which machine is home", async () => {
    const fetchImpl = reply(200, { deviceId: "01JPHONE", token: "fdt_01JPHONE.new", machine: { id: "hangar-id", name: "hangar" } });

    const restored = await restoreCredentials("https://hangar.tail1.ts.net", fetchImpl);

    expect(fetchImpl).toHaveBeenCalledWith("/api/machine/devices/me/token", { method: "POST", credentials: "include" });
    expect(restored).toMatchObject({ homeMachineId: "hangar-id", homeMachineName: "hangar", homeBaseUrl: "https://hangar.tail1.ts.net", deviceId: "01JPHONE", token: "fdt_01JPHONE.new", grants: [] });
    expect(await readCredentials()).toMatchObject({ token: "fdt_01JPHONE.new" });
  });

  it("saves nothing for a browser that isn't a paired phone, or when Fleet can't be reached", async () => {
    expect(await restoreCredentials("https://hangar", reply(400, { error: "Only a paired device has a device token." }))).toBeNull();
    expect(await restoreCredentials("https://hangar", vi.fn(async () => { throw new TypeError("offline"); }) as unknown as typeof fetch)).toBeNull();
    expect(await restoreCredentials("https://hangar", reply(200, { token: "" }))).toBeNull();
    expect(await readCredentials()).toBeNull();
  });
});
