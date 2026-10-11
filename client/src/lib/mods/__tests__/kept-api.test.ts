import { beforeEach, describe, expect, it, vi } from "vitest";
import type { MachineConnection } from "@/lib/machines";

const { apiFetchOnMock } = vi.hoisted(() => ({ apiFetchOnMock: vi.fn() }));
vi.mock("@/lib/api-client", () => ({ apiFetchOn: apiFetchOnMock }));

import {
  checkDraft,
  dismissKeepRequest,
  fetchDrafts,
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
    apiFetchOnMock.mockImplementation(async () => json({ on: true, safeMode: false }));
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
  it("fetchDrafts reads a session's drafts from its machine", async () => {
    apiFetchOnMock.mockResolvedValueOnce(json([{ sessionId: "s1", name: "test-chips" }]));
    expect(await fetchDrafts("s1", machine)).toHaveLength(1);
    expect(lastCall()[0]).toBe(machine);
    expect(lastCall()[1]).toBe("/api/sessions/s1/mods/drafts");
  });

  it("setDraftOn POSTs on or off, encoding the name", async () => {
    apiFetchOnMock.mockImplementation(async () => json({ name: "test chips" }));
    await setDraftOn("s1", "test chips", false, machine);
    expect(lastCall()[0]).toBe(machine);
    expect(lastCall()[1]).toBe("/api/sessions/s1/mods/drafts/test%20chips/off");
    expect(lastCall()[2]?.method).toBe("POST");
    await setDraftOn("s1", "test chips", true);
    expect(lastCall()[1]).toBe("/api/sessions/s1/mods/drafts/test%20chips/on");
  });
});

describe("check, keep and dismiss", () => {
  it("checkDraft reads the report, null when there is no checker", async () => {
    apiFetchOnMock.mockResolvedValueOnce(json({ check: { ok: true, name: "test-chips" } }));
    expect((await checkDraft("s1", "test-chips", machine))?.name).toBe("test-chips");
    expect(lastCall()[0]).toBe(machine);
    expect(lastCall()[1]).toBe("/api/sessions/s1/mods/drafts/test-chips/check");
    apiFetchOnMock.mockResolvedValueOnce(json({ check: null }));
    expect(await checkDraft("s1", "test-chips")).toBeNull();
  });

  it("keepDraft POSTs the trimmed note, or no body when it is empty", async () => {
    apiFetchOnMock.mockImplementation(async () => json({ name: "test-chips" }));
    await keepDraft("s1", "test-chips", "  Easier to scan.  ", machine);
    expect(lastCall()[1]).toBe("/api/sessions/s1/mods/drafts/test-chips/keep");
    expect(lastCall()[2]?.method).toBe("POST");
    expect(JSON.parse(lastCall()[2]?.body as string)).toEqual({ note: "Easier to scan." });
    await keepDraft("s1", "test-chips", "   ");
    expect(lastCall()[2]?.body).toBeUndefined();
  });

  it("dismissKeepRequest DELETEs, accepts an empty 204 and treats 404 as done", async () => {
    apiFetchOnMock.mockResolvedValueOnce(new Response(null, { status: 204 }));
    await expect(dismissKeepRequest("s1", "test-chips", machine)).resolves.toBeUndefined();
    expect(lastCall()[1]).toBe("/api/sessions/s1/mods/drafts/test-chips/keep-request");
    expect(lastCall()[2]?.method).toBe("DELETE");
    apiFetchOnMock.mockResolvedValueOnce(json({ error: "gone" }, 404));
    await expect(dismissKeepRequest("s1", "test-chips")).resolves.toBeUndefined();
    apiFetchOnMock.mockResolvedValueOnce(json({ error: "Nope." }, 400));
    await expect(dismissKeepRequest("s1", "test-chips")).rejects.toMatchObject({ status: 400 });
  });
});

describe("errors", () => {
  it("throws ModsRequestError with the server's message and status", async () => {
    apiFetchOnMock.mockImplementation(async () => json({ error: "That draft is gone." }, 404));
    const error = await setDraftOn("s1", "nope", true).catch((e: unknown) => e);
    expect(error).toBeInstanceOf(ModsRequestError);
    expect((error as ModsRequestError).message).toBe("That draft is gone.");
    expect((error as ModsRequestError).status).toBe(404);
  });
});

describe("kept mods", () => {
  it("fetchMods reads the list from the given machine", async () => {
    apiFetchOnMock.mockImplementation(async () => json({ safeMode: false, mods: [] }));
    expect(await fetchMods(machine)).toEqual({ safeMode: false, mods: [] });
    expect(lastCall()[0]).toBe(machine);
    expect(lastCall()[1]).toBe("/api/mods");
  });

  it("undoMod POSTs, encoding the name", async () => {
    apiFetchOnMock.mockImplementation(async () => json({ name: "test chips" }));
    await undoMod("test chips");
    expect(lastCall()[1]).toBe("/api/mods/test%20chips/undo");
    expect(lastCall()[2]?.method).toBe("POST");
  });

  it("setModOn picks on or off", async () => {
    apiFetchOnMock.mockImplementation(async () => json({ name: "test-chips" }));
    await setModOn("test-chips", true);
    expect(lastCall()[1]).toBe("/api/mods/test-chips/on");
    await setModOn("test-chips", false);
    expect(lastCall()[1]).toBe("/api/mods/test-chips/off");
  });

  it("setSafeMode PUTs { on }", async () => {
    apiFetchOnMock.mockImplementation(async () => json({ safeMode: true, mods: [] }));
    expect(await setSafeMode(true)).toEqual({ safeMode: true, mods: [] });
    const [, path, init] = lastCall();
    expect(path).toBe("/api/mods/safe-mode");
    expect(init?.method).toBe("PUT");
    expect(JSON.parse(init?.body as string)).toEqual({ on: true });
    expect(new Headers(init?.headers).get("Content-Type")).toBe("application/json");
  });

});
