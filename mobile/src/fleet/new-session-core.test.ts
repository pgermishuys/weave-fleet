import { describe, expect, test } from "bun:test";
import type { HarnessInfo, SessionListItem } from "@fleet/api/client";
import type { KeyValueStorage } from "./machines-core";
import { NewSessionRecents, blockerOf, chooseFolder, chooseHarness, chooseMachine, folderFor, readyHarnesses, recentFolders } from "./new-session-core";

const memory = () => {
  const data = new Map<string, string>();
  const storage: KeyValueStorage = { get: async (k) => data.get(k) ?? null, set: async (k, v) => void data.set(k, v), remove: async (k) => void data.delete(k) };
  return { data, storage };
};
const harness = (type: string, state: string, isDefault = false) => ({ type, displayName: type, available: state === "ready", userEnabled: true, state, isDefault }) as unknown as HarnessInfo;
const session = (folder: string, updated: number, extra: Partial<SessionListItem> = {}) =>
  ({ workspaceDirectory: folder, sourceDirectory: null, parentSessionId: null, session: { time: { created: updated, updated } }, ...extra }) as unknown as SessionListItem;

describe("chooseMachine", () => {
  const studio = { machineId: "a", state: "connected" };
  const laptop = { machineId: "b", state: "offline" };
  const desk = { machineId: "c", state: "connected" };
  test("the last one started on, when it's connected", () => expect(chooseMachine([studio, laptop, desk], "c")).toBe(desk));
  test("else the first connected one", () => {
    expect(chooseMachine([laptop, studio, desk], "b")).toBe(studio);
    expect(chooseMachine([laptop, studio], undefined)).toBe(studio);
  });
  test("with none connected, the last one (so its reason shows), else the first", () => {
    expect(chooseMachine([laptop, { machineId: "d", state: "offline" }], "b")).toBe(laptop);
    expect(chooseMachine([laptop], undefined)).toBe(laptop);
    expect(chooseMachine([], undefined)).toBeUndefined();
  });
});

describe("chooseHarness", () => {
  test("the machine's default when it's ready", () => {
    expect(chooseHarness([harness("opencode2", "ready"), harness("claude-code", "ready", true)])?.type).toBe("claude-code");
  });
  test("else the first ready one in Fleet's order", () => {
    const all = [harness("pi", "ready"), harness("copilot", "ready"), harness("opencode2", "not-installed", true), harness("claude-code", "ready")];
    expect(chooseHarness(all)?.type).toBe("claude-code");
    expect(readyHarnesses([...all, harness("opencode", "ready")]).map((h) => h.type)).toEqual(["opencode", "claude-code", "copilot", "pi"]);
  });
  test("none when nothing is ready, and a harness the user turned off isn't", () => {
    expect(chooseHarness([harness("opencode2", "sign-in-required", true)])).toBeUndefined();
    expect(chooseHarness([{ ...harness("opencode2", "ready", true), userEnabled: false } as HarnessInfo])).toBeUndefined();
  });
});

describe("folders", () => {
  const sessions = [session("/code/billing-service", 10), session("/code/demo-app", 30), session("/code/docs-site", 20, { sourceDirectory: "/code/docs-site-src" }), session("/code/child", 99, { parentSessionId: "x" })];
  test("the phone's last folder wins; the first time it's the newest session's", () => {
    expect(chooseFolder(["/code/docs-site"], sessions)).toBe("/code/docs-site");
    expect(chooseFolder([], sessions)).toBe("/code/demo-app");
  });
  test("a machine with no sessions and no history gets none", () => expect(chooseFolder([], [])).toBeUndefined());
  test("recent: the phone's own first, then the newest sessions' (a worktree's repository, not the worktree), no repeats", () => {
    expect(recentFolders(["/code/billing-service"], sessions)).toEqual(["/code/billing-service", "/code/demo-app", "/code/docs-site-src"]);
  });
  test("a scanned repository can get a worktree, any other folder is used as it is", () => {
    const repositories = [{ name: "demo-app", path: "/code/demo-app", parentRoot: "/code" }];
    expect(folderFor("/code/demo-app", repositories)).toEqual({ kind: "repository", path: "/code/demo-app" });
    expect(folderFor("/tmp/notes", repositories)).toEqual({ kind: "directory", path: "/tmp/notes" });
  });
});

describe("blockerOf", () => {
  const ok = { machineName: "studio-pc", state: "connected", otherMachines: true, harnessesLoaded: true, harnessReady: true, hasFolder: true };
  test("nothing blocks a connected machine with a ready harness", () => expect(blockerOf(ok)).toBeNull());
  test("an offline machine says so and what to do", () => {
    const blocker = blockerOf({ ...ok, machineName: "travel-laptop", state: "offline" });
    expect(`${blocker?.lead} ${blocker?.rest}`).toBe("travel-laptop is offline. Pick another machine, or wait for it to come back.");
  });
  test("with one machine paired there's no other to pick", () => {
    expect(blockerOf({ ...ok, state: "offline", otherMachines: false })?.rest).toBe("Wait for it to come back.");
  });
  test("no ready harness says to set one up on the computer", () => {
    expect(blockerOf({ ...ok, harnessReady: false })?.rest).toBe("Set one up in Fleet on that computer.");
    expect(blockerOf({ ...ok, harnessReady: false, harnessesLoaded: false })).toBeNull();
  });
});

describe("NewSessionRecents", () => {
  test("remembers the machine, the folders (newest first, no repeats) and the model per folder and harness", async () => {
    const { storage } = memory();
    const recents = new NewSessionRecents(storage);
    await recents.load(["a"]);
    await recents.remember("a", "/code/demo-app", "opencode2", { providerID: "anthropic", modelID: "sonnet" });
    await recents.remember("a", "/code/docs-site", "opencode2", null);
    await recents.remember("a", "/code/demo-app", "opencode2", null);
    const again = new NewSessionRecents(storage);
    await again.load(["a", "b"]);
    expect(again.machine()).toBe("a");
    expect(again.folders("a")).toEqual(["/code/demo-app", "/code/docs-site"]);
    expect(again.model("a", "/code/demo-app", "opencode2")).toEqual({ providerID: "anthropic", modelID: "sonnet" });
    expect(again.model("a", "/code/demo-app", "pi")).toBeUndefined();
    expect(again.folders("b")).toEqual([]);
  });
  test("keeps few, and a quick chat (no folder) only remembers the machine", async () => {
    const { storage, data } = memory();
    const recents = new NewSessionRecents(storage);
    await recents.load(["a"]);
    for (let i = 0; i < 12; i++) await recents.remember("a", `/code/p${i}`, "pi", { providerID: "x", modelID: `m${i}` });
    expect(recents.folders("a")).toHaveLength(8);
    expect(recents.folders("a")[0]).toBe("/code/p11");
    expect(recents.model("a", "/code/p0", "pi")).toBeUndefined();
    expect(recents.model("a", "/code/p11", "pi")?.modelID).toBe("m11");
    await recents.remember("a", undefined, "pi", null);
    expect(recents.folders("a")[0]).toBe("/code/p11");
    expect(data.get("fleet.new-session.machine")).toBe("a");
  });
  test("storage that fails or holds junk reads as nothing", async () => {
    const recents = new NewSessionRecents({ get: async () => { throw new Error("locked"); }, set: async () => {}, remove: async () => {} });
    await recents.load(["a"]);
    expect(recents.machine()).toBeUndefined();
    expect(recents.folders("a")).toEqual([]);
  });
});
