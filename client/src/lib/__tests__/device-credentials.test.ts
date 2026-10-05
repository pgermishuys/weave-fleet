import "fake-indexeddb/auto";
import { beforeEach, describe, expect, it } from "vitest";
import {
  clearCredentials,
  credentialFor,
  readCredentials,
  readCredentialsSync,
  removeGrant,
  saveCredentials,
  saveGrant,
  type DeviceCredentials,
} from "../device-credentials";

const paired: DeviceCredentials = {
  homeMachineId: "home",
  homeMachineName: "hangar",
  homeBaseUrl: "https://hangar.tail9c2e.ts.net",
  deviceId: "01JDEVICE",
  token: "fdt_01JDEVICE.secret",
  grants: [],
  pairedAt: "2026-10-05T09:00:00.000Z",
};

describe("device credentials", () => {
  beforeEach(async () => {
    await clearCredentials();
    localStorage.clear();
  });

  it("saves to IndexedDB and the localStorage copy", async () => {
    await saveCredentials(paired);

    expect(await readCredentials()).toEqual(paired);
    expect(readCredentialsSync()).toEqual(paired);
  });

  it("reads IndexedDB when the localStorage copy is gone, as the service worker does", async () => {
    await saveCredentials(paired);
    localStorage.clear();

    expect(readCredentialsSync()).toBeNull();
    expect(await readCredentials()).toEqual(paired);
  });

  it("clears both", async () => {
    await saveCredentials(paired);
    await clearCredentials();

    expect(await readCredentials()).toBeNull();
    expect(readCredentialsSync()).toBeNull();
  });

  it("ignores junk in the copy", () => {
    localStorage.setItem("weave:device-credentials", "{not json");
    expect(readCredentialsSync()).toBeNull();
    localStorage.setItem("weave:device-credentials", JSON.stringify({ token: "x" }));
    expect(readCredentialsSync()).toBeNull();
  });

  it("keeps one grant per machine", async () => {
    await saveCredentials(paired);

    await saveGrant({ machineId: "falcon", baseUrl: "https://falcon.ts.net", token: "fdt_a.1" });
    await saveGrant({ machineId: "falcon", baseUrl: "https://falcon.ts.net", token: "fdt_a.2" });
    await saveGrant({ machineId: "osprey", baseUrl: "https://osprey.ts.net", token: "fdt_b.1" });
    const saved = await readCredentials();

    expect(saved?.grants.map((g) => `${g.machineId}:${g.token}`)).toEqual(["falcon:fdt_a.2", "osprey:fdt_b.1"]);
    expect(credentialFor(saved!, "falcon")).toEqual({ baseUrl: "https://falcon.ts.net", token: "fdt_a.2" });
    expect(credentialFor(saved!, "home")).toEqual({ baseUrl: paired.homeBaseUrl, token: paired.token });
    expect(credentialFor(saved!, "nowhere")).toBeNull();

    await removeGrant("falcon");
    expect((await readCredentials())?.grants.map((g) => g.machineId)).toEqual(["osprey"]);
  });

  it("does nothing to grants without credentials", async () => {
    expect(await saveGrant({ machineId: "falcon", baseUrl: "https://falcon.ts.net", token: "t" })).toBeNull();
  });
});
