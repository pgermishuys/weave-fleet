import { describe, expect, it, vi } from "vitest";
import { shareInFlight } from "@/lib/shared-request";

describe("shareInFlight", () => {
  it("answers callers asking for the same key at once with one request", async () => {
    let resolve!: (value: string) => void;
    const load = vi.fn(() => new Promise<string>((done) => { resolve = done; }));
    const get = shareInFlight(load);

    const first = get("session-1");
    const second = get("session-1");
    resolve("models");

    await expect(first).resolves.toBe("models");
    await expect(second).resolves.toBe("models");
    expect(load).toHaveBeenCalledTimes(1);
  });

  it("asks again once the request has settled, and keeps keys apart", async () => {
    const load = vi.fn(async (key: string) => `models for ${key}`);
    const get = shareInFlight(load);

    await get("session-1");
    await get("session-1");
    await get("session-2");

    expect(load.mock.calls.map(([key]) => key)).toEqual(["session-1", "session-1", "session-2"]);
  });

  it("lets a failed request be tried again", async () => {
    const load = vi.fn()
      .mockRejectedValueOnce(new Error("HTTP 503"))
      .mockResolvedValueOnce("models");
    const get = shareInFlight(load);

    await expect(get("session-1")).rejects.toThrow("HTTP 503");
    await expect(get("session-1")).resolves.toBe("models");
  });
});
