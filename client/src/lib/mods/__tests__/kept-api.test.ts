import { beforeEach, describe, expect, it, vi } from "vitest";
import type { MachineConnection } from "@/lib/machines";

const { apiFetchOnMock } = vi.hoisted(() => ({ apiFetchOnMock: vi.fn() }));
vi.mock("@/lib/api-client", () => ({ apiFetchOn: apiFetchOnMock }));

import {
  activateModVersion,
  checkDraft,
  fetchDraftFiles,
  fetchDrafts,
  fetchModVersionFiles,
  fetchMods,
  fetchModsSwitch,
  keepDraft,
  ModsRequestError,
  setDraftOn,
  setModOn,
  setSafeMode,
  undoMod,
} from "@/lib/mods/kept-api";

const json = (body: unknown, status = 200) =>
  new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });

const machine = { id: "m1" } as unknown as MachineConnection;

function lastCall(): [MachineConnection | null, string, RequestInit | undefined] {
  return apiFetchOnMock.mock.calls.at(-1) as [MachineConnection | null, string, RequestInit | undefined];
}

beforeEach(() => {
  apiFetchOnMock.mockReset();
});

describe("fetchModsSwitch", () => {
  it("reads the switch", async () => {
    apiFetchOnMock.mockResolvedValue(json({ on: true, safeMode: false }));
    expect(await fetchModsSwitch(machine)).toEqual({ on: true, safeMode: false });
    expect(lastCall()[0]).toBe(machine);
    expect(lastCall()[1]).toBe("/api/features/mods");
  });

  it("is null on 404, a network failure or bad JSON", async () => {
    apiFetchOnMock.mockResolvedValueOnce(json({ error: "gone" }, 404));
    expect(await fetchModsSwitch()).toBeNull();
    apiFetchOnMock.mockRejectedValueOnce(new TypeError("offline"));
    expect(await fetchModsSwitch()).toBeNull();
    apiFetchOnMock.mockResolvedValueOnce(new Response("<html>", { status: 200 }));
    expect(await fetchModsSwitch()).toBeNull();
  });
});

describe("requests", () => {
  it("fetchMods", async () => {
    apiFetchOnMock.mockResolvedValue(json({ safeMode: false, mods: [] }));
    expect(await fetchMods()).toEqual({ safeMode: false, mods: [] });
    expect(lastCall()[0]).toBeNull();
    expect(lastCall()[1]).toBe("/api/mods");
  });

  it("fetchModVersionFiles unwraps files and encodes the name", async () => {
    apiFetchOnMock.mockResolvedValue(json({ files: [{ path: "index.ts", content: "x" }] }));
    expect(await fetchModVersionFiles("a b/c", 2, machine)).toEqual([{ path: "index.ts", content: "x" }]);
    expect(lastCall()[1]).toBe("/api/mods/a%20b%2Fc/versions/2/files");
  });

  it("activateModVersion PUTs the version", async () => {
    apiFetchOnMock.mockResolvedValue(json({ name: "test-chips" }));
    await activateModVersion("test-chips", 2);
    const [, path, init] = lastCall();
    expect(path).toBe("/api/mods/test-chips/active");
    expect(init?.method).toBe("PUT");
    expect(JSON.parse(init?.body as string)).toEqual({ version: 2 });
    expect(new Headers(init?.headers).get("Content-Type")).toBe("application/json");
  });

  it("undoMod POSTs", async () => {
    apiFetchOnMock.mockResolvedValue(json({ name: "test-chips" }));
    await undoMod("test-chips");
    expect(lastCall()[1]).toBe("/api/mods/test-chips/undo");
    expect(lastCall()[2]?.method).toBe("POST");
  });

  it("setModOn picks on or off", async () => {
    apiFetchOnMock.mockResolvedValue(json({ name: "test-chips" }));
    await setModOn("test-chips", true);
    expect(lastCall()[1]).toBe("/api/mods/test-chips/on");
    await setModOn("test-chips", false);
    expect(lastCall()[1]).toBe("/api/mods/test-chips/off");
    expect(lastCall()[2]?.method).toBe("POST");
  });

  it("setSafeMode PUTs { on }", async () => {
    apiFetchOnMock.mockResolvedValue(json({ safeMode: true, mods: [] }));
    expect(await setSafeMode(true)).toEqual({ safeMode: true, mods: [] });
    const [, path, init] = lastCall();
    expect(path).toBe("/api/mods/safe-mode");
    expect(init?.method).toBe("PUT");
    expect(JSON.parse(init?.body as string)).toEqual({ on: true });
  });

  it("fetchDrafts and fetchDraftFiles", async () => {
    apiFetchOnMock.mockResolvedValueOnce(json([{ sessionId: "s1", name: "test-chips" }]));
    expect(await fetchDrafts("s1", machine)).toHaveLength(1);
    expect(lastCall()[1]).toBe("/api/sessions/s1/mods/drafts");
    apiFetchOnMock.mockResolvedValueOnce(json({ files: [] }));
    expect(await fetchDraftFiles("s1", "test chips")).toEqual([]);
    expect(lastCall()[1]).toBe("/api/sessions/s1/mods/drafts/test%20chips/files");
  });

  it("checkDraft unwraps the report, null while there is no checker", async () => {
    apiFetchOnMock.mockResolvedValueOnce(json({ check: { ok: true } }));
    expect(await checkDraft("s1", "test-chips")).toEqual({ ok: true });
    expect(lastCall()[1]).toBe("/api/sessions/s1/mods/drafts/test-chips/check");
    apiFetchOnMock.mockResolvedValueOnce(json({ check: null }));
    expect(await checkDraft("s1", "test-chips")).toBeNull();
  });

  it("keepDraft sends the note, or no body when it is empty", async () => {
    apiFetchOnMock.mockResolvedValue(json({ name: "test-chips" }));
    await keepDraft("s1", "test-chips", "show failing names");
    let [, path, init] = lastCall();
    expect(path).toBe("/api/sessions/s1/mods/drafts/test-chips/keep");
    expect(init?.method).toBe("POST");
    expect(JSON.parse(init?.body as string)).toEqual({ note: "show failing names" });
    await keepDraft("s1", "test-chips", "  ");
    [, , init] = lastCall();
    expect(init?.body).toBeUndefined();
  });

  it("setDraftOn picks on or off", async () => {
    apiFetchOnMock.mockResolvedValue(json({ name: "test-chips" }));
    await setDraftOn("s1", "test-chips", false, machine);
    expect(lastCall()[0]).toBe(machine);
    expect(lastCall()[1]).toBe("/api/sessions/s1/mods/drafts/test-chips/off");
    await setDraftOn("s1", "test-chips", true);
    expect(lastCall()[1]).toBe("/api/sessions/s1/mods/drafts/test-chips/on");
  });
});

describe("errors", () => {
  it("throws ModsRequestError with the server's message and status", async () => {
    apiFetchOnMock.mockResolvedValue(json({ error: "No such mod." }, 404));
    const error = await undoMod("nope").catch((e: unknown) => e);
    expect(error).toBeInstanceOf(ModsRequestError);
    expect((error as ModsRequestError).message).toBe("No such mod.");
    expect((error as ModsRequestError).status).toBe(404);
  });

  it("falls back to a plain message when the body isn't JSON", async () => {
    apiFetchOnMock.mockResolvedValue(new Response("boom", { status: 502 }));
    const error = (await fetchMods().catch((e: unknown) => e)) as ModsRequestError;
    expect(error).toBeInstanceOf(ModsRequestError);
    expect(error.status).toBe(502);
    expect(error.message).not.toBe("");
  });
});
