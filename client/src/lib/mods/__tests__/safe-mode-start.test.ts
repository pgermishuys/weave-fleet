import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

const { apiFetchMock } = vi.hoisted(() => ({ apiFetchMock: vi.fn() }));
vi.mock("@/lib/api-client", () => ({ apiFetchOn: apiFetchMock }));

import { START_WITHOUT_MODS_KEY, START_WITHOUT_MODS_TIMEOUT_MS, startWithoutModsFromAddress } from "@/lib/mods/safe-mode-start";

const answer = (status: number) => new Response("{}", { status });
const pending = () => sessionStorage.getItem(START_WITHOUT_MODS_KEY);
const at = (url: string) => window.history.replaceState({ keep: 1 }, "", url);

describe("start without mods, early", () => {
  beforeEach(() => {
    apiFetchMock.mockReset();
    apiFetchMock.mockResolvedValue(answer(200));
    sessionStorage.clear();
    at("/");
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it.each(["mods=on", "mods=offf", "other=off"])("ignores %s", async (query) => {
    at(`/?${query}`);
    await startWithoutModsFromAddress();
    expect(apiFetchMock).not.toHaveBeenCalled();
    expect(window.location.search).toBe(`?${query}`);
  });

  it("puts safe mode on once (any case) on the home machine, and takes only that parameter off the address", async () => {
    at("/sessions/abc?tab=files&mods=OFF&x=1#line-4");

    await startWithoutModsFromAddress();

    expect(apiFetchMock).toHaveBeenCalledTimes(1);
    const [machine, path, init] = apiFetchMock.mock.calls[0] as [unknown, string, RequestInit];
    expect(machine).toBeNull();
    expect(path).toBe("/api/mods/safe-mode");
    expect(init.method).toBe("PUT");
    expect(JSON.parse(init.body as string)).toEqual({ on: true });
    expect(pending()).toBeNull();
    expect(window.location.pathname + window.location.search + window.location.hash).toBe("/sessions/abc?tab=files&x=1#line-4");
    expect(window.history.state).toEqual({ keep: 1 });
  });

  it("drops the request when the Mods switch is off (404)", async () => {
    apiFetchMock.mockResolvedValue(answer(404));
    at("/?mods=off");
    await startWithoutModsFromAddress();
    expect(pending()).toBeNull();
  });

  it("keeps the request when the server says 401, and retries it on the next start", async () => {
    apiFetchMock.mockResolvedValueOnce(answer(401));
    at("/?mods=off");
    await startWithoutModsFromAddress();
    expect(pending()).not.toBeNull();
    expect(window.location.search).toBe("");

    await startWithoutModsFromAddress();

    expect(apiFetchMock).toHaveBeenCalledTimes(2);
    expect(pending()).toBeNull();
  });

  it("keeps the request when the network fails", async () => {
    apiFetchMock.mockRejectedValue(new TypeError("Failed to fetch"));
    at("/?mods=off");
    await expect(startWithoutModsFromAddress()).resolves.toBeUndefined();
    expect(pending()).not.toBeNull();
  });

  it("stops waiting after a few seconds, so a slow server never blocks the app, and keeps the request", async () => {
    vi.useFakeTimers();
    apiFetchMock.mockImplementation((_machine: unknown, _path: string, init: RequestInit) =>
      new Promise((_resolve, reject) => {
        init.signal?.addEventListener("abort", () => reject(new DOMException("Aborted", "AbortError")));
      }));
    at("/?mods=off");

    const started = startWithoutModsFromAddress();
    await vi.advanceTimersByTimeAsync(START_WITHOUT_MODS_TIMEOUT_MS + 100);
    await started;

    expect(pending()).not.toBeNull();
  });
});
