import { describe, expect, it } from "vitest";
import type { DeviceCredentials } from "@/lib/device-credentials";
import { opensAsPhoneApp } from "@/routes/index";
import { folderFromId, folderId, folderName, folderOptions, phoneMachines } from "../new-session";

const credentials: DeviceCredentials = {
  homeMachineId: "hangar-id",
  homeMachineName: "hangar",
  homeBaseUrl: "https://hangar.tail1.ts.net",
  deviceId: "d1",
  token: "fdt_home.1",
  grants: [
    { machineId: "kestrel-id", baseUrl: "https://kestrel.tail1.ts.net", token: "fdt_kestrel.1" },
    { machineId: "falcon-id", baseUrl: "https://falcon.tail1.ts.net", token: "fdt_falcon.1" },
  ],
  pairedAt: "2026-10-05T09:00:00Z",
};

const repositories = [
  { name: "weave-fleet", path: "/home/me/src/weave-fleet", parentRoot: "/home/me/src" },
  { name: "dotfiles", path: "/home/me/src/dotfiles", parentRoot: "/home/me/src" },
];

describe("machines the phone can start a session on", () => {
  it("lists home first, then the machines it has a key for, by name", () => {
    const machines = phoneMachines(credentials, [
      { id: "falcon-id", name: "falcon", baseUrl: "https://falcon.tail1.ts.net" },
      { id: "kestrel-id", name: "kestrel", baseUrl: "https://kestrel.tail1.ts.net" },
      { id: "osprey-id", name: "osprey", baseUrl: "https://osprey.tail1.ts.net" },
    ]);

    expect(machines.map((machine) => machine.name)).toEqual(["hangar", "falcon", "kestrel"]);
    expect(machines[0]?.connection).toBeNull();
    expect(machines[1]?.connection).toMatchObject({ id: "falcon-id", baseUrl: "https://falcon.tail1.ts.net", token: "fdt_falcon.1" });
  });

  it("names a machine home's list doesn't know yet from its address", () => {
    expect(phoneMachines(credentials, []).map((machine) => machine.name)).toEqual(["hangar", "falcon", "kestrel"]);
  });

  it("has nothing to offer without pairing", () => {
    expect(phoneMachines(null, [])).toEqual([]);
  });
});

describe("the folder sheet", () => {
  it("puts recent folders first, then the other repositories by name, then no folder", () => {
    const options = folderOptions(repositories, [{ kind: "repository", path: "/home/me/src/weave-fleet" }, { kind: "directory", path: "/tmp/scratch" }]);

    expect(options.map((option) => option.label)).toEqual(["weave-fleet", "scratch", "dotfiles", "No folder"]);
    expect(options.map((option) => option.id)).toEqual([
      "repository:/home/me/src/weave-fleet",
      "directory:/tmp/scratch",
      "repository:/home/me/src/dotfiles",
      "none",
    ]);
  });

  it("turns ids back into folders", () => {
    for (const folder of [{ kind: "repository", path: "/a/b" }, { kind: "directory", path: "C:\\work\\x" }, { kind: "none" }] as const) {
      expect(folderFromId(folderId(folder))).toEqual(folder);
    }
  });

  it("names a folder by its repository, else its last part", () => {
    expect(folderName({ kind: "repository", path: "/home/me/src/weave-fleet" }, repositories)).toBe("weave-fleet");
    expect(folderName({ kind: "directory", path: "C:\\work\\notes\\" }, repositories)).toBe("notes");
    expect(folderName({ kind: "none" }, repositories)).toBe("No folder");
  });
});

describe("opening the installed app", () => {
  const phone = (query: string) => query === "(display-mode: standalone)" || query === "(max-width: 716px)";

  it("goes to the phone inbox, unless the full Fleet was asked for", () => {
    expect(opensAsPhoneApp("", phone)).toBe(true);
    expect(opensAsPhoneApp("?view=full", phone)).toBe(false);
  });
});
