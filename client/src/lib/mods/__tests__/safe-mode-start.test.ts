import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

const { apiFetchMock } = vi.hoisted(() => ({ apiFetchMock: vi.fn() }));
vi.mock("@/lib/api-client", () => ({ apiFetchOn: apiFetchMock }));

import {
  applyPendingStartWithoutMods,
  START_WITHOUT_MODS_KEY,
  START_WITHOUT_MODS_TIMEOUT_MS,
  startWithoutModsFromAddress,
} from "@/lib/mods/safe-mode-start";

const answer = (status: number) => new Response("{}", { status, headers: { "Content-Type": "application/json" } });
const pending = () => sessionStorage.getItem(START_WITHOUT_MODS_KEY);

function at(url: string): void {
  window.history.replaceState({ keep: 1 }, "", url);
}

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

  it("does nothing when the address doesn't ask for it", async () => {
    at("/sessions?tab=files");

    await startWithoutModsFromAddress();

    expect(apiFetchMock).not.toHaveBeenCalled();
    expect(pending()).toBeNull();
  });

  it.each(["mods=on", "mods=", "mods=offf", "mods=no", "other=off"])("ignores %s", async (query) => {
    at(`/?${query}`);

    await startWithoutModsFromAddress();

    expect(apiFetchMock).not.toHaveBeenCalled();
    expect(window.location.search).toBe(`?${query}`);
  });

  it("puts safe mode on once, with the body the server reads", async () => {
    at("/?mods=off");

    await startWithoutModsFromAddress();

    expect(apiFetchMock).toHaveBeenCalledTimes(1);
    const [machine, path, init] = apiFetchMock.mock.calls[0] as [unknown, string, RequestInit];
    expect(machine).toBeNull();
    expect(path).toBe("/api/mods/safe-mode");
    expect(init.method).toBe("PUT");
    expect(JSON.parse(init.body as string)).toEqual({ on: true });
    expect(new Headers(init.headers).get("Content-Type")).toBe("application/json");
    expect(pending()).toBeNull();
  });

  it("reads the value in any case", async () => {
    at("/?mods=OFF");

    await startWithoutModsFromAddress();

    expect(apiFetchMock).toHaveBeenCalledTimes(1);
  });

  it("takes only that parameter off the address, keeping the path, the other parameters and the hash", async () => {
    at("/sessions/abc?tab=files&mods=off&x=1#line-4");

    await startWithoutModsFromAddress();

    expect(window.location.pathname + window.location.search + window.location.hash).toBe("/sessions/abc?tab=files&x=1#line-4");
    expect(window.history.state).toEqual({ keep: 1 });
  });

  it("leaves a clean address when it was the only parameter", async () => {
    at("/sessions?mods=off");

    await startWithoutModsFromAddress();

    expect(window.location.pathname + window.location.search).toBe("/sessions");
  });

  it("drops the request when the Mods switch is off (404)", async () => {
    apiFetchMock.mockResolvedValue(answer(404));
    at("/?mods=off");

    await startWithoutModsFromAddress();

    expect(pending()).toBeNull();
  });

  it.each([401, 403])("keeps the request for after sign-in when the server answers %i", async (status) => {
    apiFetchMock.mockResolvedValue(answer(status));
    at("/?mods=off");

    await startWithoutModsFromAddress();

    expect(pending()).not.toBeNull();
    expect(window.location.search).toBe("");
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

    let done = false;
    const started = startWithoutModsFromAddress().then(() => { done = true; });
    await vi.advanceTimersByTimeAsync(START_WITHOUT_MODS_TIMEOUT_MS - 100);
    expect(done).toBe(false);
    await vi.advanceTimersByTimeAsync(200);
    await started;

    expect(done).toBe(true);
    expect(pending()).not.toBeNull();
  });

  it("retries a kept request on the next start, then clears it", async () => {
    apiFetchMock.mockResolvedValueOnce(answer(401));
    at("/?mods=off");
    await startWithoutModsFromAddress();
    expect(pending()).not.toBeNull();

    apiFetchMock.mockResolvedValueOnce(answer(200));
    await startWithoutModsFromAddress();

    expect(apiFetchMock).toHaveBeenCalledTimes(2);
    expect(pending()).toBeNull();

    await startWithoutModsFromAddress();
    expect(apiFetchMock).toHaveBeenCalledTimes(2);
  });

  it("applyPendingStartWithoutMods sends nothing when nothing is waiting", async () => {
    await applyPendingStartWithoutMods();

    expect(apiFetchMock).not.toHaveBeenCalled();
  });
});
