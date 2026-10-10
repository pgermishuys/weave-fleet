import { beforeEach, describe, expect, it, vi } from "vitest";

const apiFetchOn = vi.hoisted(() => vi.fn());
vi.mock("@/lib/api-client", () => ({ apiFetchOn }));

const { fetchModLog } = await import("@/lib/mods/mod-log");
const { ModsRequestError } = await import("@/lib/mods/kept-api");

const reply = (status: number, body: unknown) => new Response(JSON.stringify(body), { status });

beforeEach(() => vi.clearAllMocks());

describe("fetchModLog", () => {
  it("reads the lines from the mod's log route", async () => {
    apiFetchOn.mockResolvedValue(reply(200, [{ at: "t", level: "info", text: "hi" }]));
    expect(await fetchModLog("test-chips")).toEqual([{ at: "t", level: "info", text: "hi" }]);
    expect(apiFetchOn).toHaveBeenCalledWith(null, "/api/mods/test-chips/log");
  });

  it("is null on a 404: the route isn't there yet, or the mod has no log", async () => {
    apiFetchOn.mockResolvedValue(reply(404, { error: "No log" }));
    expect(await fetchModLog("test-chips")).toBeNull();
  });

  it("throws the server's words on another failure", async () => {
    apiFetchOn.mockResolvedValue(reply(400, { error: "Bad name." }));
    await expect(fetchModLog("test-chips")).rejects.toBeInstanceOf(ModsRequestError);
  });
});
