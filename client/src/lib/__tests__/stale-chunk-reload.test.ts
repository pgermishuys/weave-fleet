import { describe, expect, it, vi } from "vitest";
import { installStaleChunkReload, isChunkLoadFailure, reloadOnceForStaleChunk } from "@/lib/stale-chunk-reload";

function environment(now = 1_000_000) {
  const store = new Map<string, string>();
  const listeners = new Map<string, (event: never) => void>();
  const env = {
    addEventListener: (type: string, listener: (event: never) => void) => listeners.set(type, listener),
    reload: vi.fn(),
    storage: { getItem: (key: string) => store.get(key) ?? null, setItem: (key: string, value: string) => void store.set(key, value) },
    now: () => now,
  };
  return { env, listeners, advance: (ms: number) => { now += ms; } };
}

describe("isChunkLoadFailure", () => {
  it("recognises the messages Chrome, Firefox and Safari give", () => {
    expect(isChunkLoadFailure(new TypeError("Failed to fetch dynamically imported module: http://x/assets/FileCanvas-abc.js"))).toBe(true);
    expect(isChunkLoadFailure(new TypeError("error loading dynamically imported module"))).toBe(true);
    expect(isChunkLoadFailure(new TypeError("Importing a module script failed."))).toBe(true);
    expect(isChunkLoadFailure(new Error("network down"))).toBe(false);
    expect(isChunkLoadFailure(undefined)).toBe(false);
  });
});

describe("reloadOnceForStaleChunk", () => {
  it("reloads, then refuses to reload again within the gap so a missing chunk can't loop", () => {
    const { env, advance } = environment();
    expect(reloadOnceForStaleChunk(env)).toBe(true);
    expect(reloadOnceForStaleChunk(env)).toBe(false);
    expect(env.reload).toHaveBeenCalledTimes(1);
    advance(61_000);
    expect(reloadOnceForStaleChunk(env)).toBe(true);
    expect(env.reload).toHaveBeenCalledTimes(2);
  });

  it("does not reload when storage is blocked", () => {
    const { env } = environment();
    env.storage.getItem = () => { throw new Error("blocked"); };
    expect(reloadOnceForStaleChunk(env)).toBe(false);
    expect(env.reload).not.toHaveBeenCalled();
  });
});

describe("installStaleChunkReload", () => {
  it("reloads on vite:preloadError and cancels the error", () => {
    const { env, listeners } = environment();
    installStaleChunkReload(env);
    const event = { preventDefault: vi.fn() };
    listeners.get("vite:preloadError")!(event as never);
    expect(env.reload).toHaveBeenCalledTimes(1);
    expect(event.preventDefault).toHaveBeenCalled();
  });

  it("lets the error through when it already reloaded a moment ago", () => {
    const { env, listeners } = environment();
    installStaleChunkReload(env);
    listeners.get("vite:preloadError")!({ preventDefault: vi.fn() } as never);
    const event = { preventDefault: vi.fn() };
    listeners.get("vite:preloadError")!(event as never);
    expect(env.reload).toHaveBeenCalledTimes(1);
    expect(event.preventDefault).not.toHaveBeenCalled();
  });

  it("reloads on an unhandled chunk-import rejection but ignores other rejections", () => {
    const { env, listeners } = environment();
    installStaleChunkReload(env);
    listeners.get("unhandledrejection")!({ reason: new Error("boom") } as never);
    expect(env.reload).not.toHaveBeenCalled();
    listeners.get("unhandledrejection")!({ reason: new TypeError("Failed to fetch dynamically imported module: /a.js") } as never);
    expect(env.reload).toHaveBeenCalledTimes(1);
  });
});
