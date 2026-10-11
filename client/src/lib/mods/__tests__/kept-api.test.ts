import { beforeEach, describe, expect, it, vi } from "vitest";
import type { MachineConnection } from "@/lib/machines";

const { apiFetchOnMock } = vi.hoisted(() => ({ apiFetchOnMock: vi.fn() }));
vi.mock("@/lib/api-client", () => ({ apiFetchOn: apiFetchOnMock }));

import { fetchDrafts, fetchModsSwitch, ModsRequestError, setDraftOn } from "@/lib/mods/kept-api";

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

describe("errors", () => {
  it("throws ModsRequestError with the server's message and status", async () => {
    apiFetchOnMock.mockImplementation(async () => json({ error: "That draft is gone." }, 404));
    const error = await setDraftOn("s1", "nope", true).catch((e: unknown) => e);
    expect(error).toBeInstanceOf(ModsRequestError);
    expect((error as ModsRequestError).message).toBe("That draft is gone.");
    expect((error as ModsRequestError).status).toBe(404);
  });
});
