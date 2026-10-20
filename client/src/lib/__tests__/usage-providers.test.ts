import { describe, expect, it } from "vitest";
import { expectedShare, formatDuration, paceNote, providerWindowLabel, tightestWindow, toUsageLimits, usageTone, windowPercent } from "@/lib/usage-providers";

const at = (hours: number) => new Date(Date.now() + hours * 3_600_000).toISOString();

describe("toUsageLimits", () => {
  it("reads the contract's body", () => {
    const limits = toUsageLimits({
      enabled: true,
      providers: [{
        provider: "openrouter", account: null, name: "OpenRouter", state: "ok", source: "OpenCode", harnesses: ["opencode"],
        readAt: at(0), windows: [], credits: { remaining: 7.6, limit: 10, used: 2.4 },
      }],
    });
    expect(limits?.providers[0]).toMatchObject({ provider: "openrouter", source: "OpenCode", credits: { remaining: 7.6, limit: 10 } });
    expect(limits?.providers[0].readAt).toBeTypeOf("number");
  });

  it("refuses what isn't a body, and skips entries and windows that don't read", () => {
    expect(toUsageLimits(null)).toBeNull();
    expect(toUsageLimits({ providers: [] })).toBeNull();
    const limits = toUsageLimits({
      enabled: true,
      providers: [null, { name: "no id" }, { provider: "codex", state: "from the future", windows: [{ utilization: 1 }, { window: "primary", utilization: 4 }] }],
    });
    expect(limits?.providers).toHaveLength(1);
    expect(limits?.providers[0].state).toBe("unreadable");
    expect(limits?.providers[0].windows).toEqual([{ window: "primary", utilization: 1, resetsAt: null, windowSeconds: null, status: "allowed" }]);
  });
});

describe("tightestWindow", () => {
  const provider = (name: string, state: string, windows: unknown[]) =>
    toUsageLimits({ enabled: true, providers: [{ provider: name, name, state, windows }] })!.providers[0];

  it("picks the fullest live window of a readable provider", () => {
    const now = Date.now();
    const found = tightestWindow(
      [
        provider("claude", "ok", [{ window: "five_hour", utilization: 0.4, resetsAt: at(1) }, { window: "seven_day", utilization: 0.9, resetsAt: at(1) }]),
        provider("codex", "not_allowed", [{ window: "primary", utilization: 0.99, resetsAt: at(1) }]),
        provider("old", "ok", [{ window: "primary", utilization: 1, resetsAt: at(-1) }]),
      ],
      now,
    );
    expect(found?.provider.provider).toBe("claude");
    expect(found?.window.window).toBe("seven_day");
    expect(tightestWindow([], now)).toBeNull();
  });

  it("is quiet below 80%, amber from there and red when used up", () => {
    const w = (utilization: number, status = "allowed") => ({ window: "x", utilization, resetsAt: null, status }) as never;
    expect(usageTone(w(0.79))).toBe("quiet");
    expect(usageTone(w(0.8))).toBe("warn");
    expect(usageTone(w(0.3, "rejected"))).toBe("used");
    expect(windowPercent(w(0.3, "rejected"))).toBe(100);
  });

  it("names Codex's windows", () => {
    expect(providerWindowLabel("primary")).toBe("Primary limit");
    expect(providerWindowLabel("five_hour")).toBe("5-hour limit");
  });
});

describe("pace", () => {
  const now = 1_000_000_000_000;
  // A 10 h window with `left` hours to go.
  const win = (utilization: number | null, left: number, over: Record<string, unknown> = {}) => ({
    window: "five_hour", utilization, resetsAt: now + left * 3_600_000, windowSeconds: 36000, status: "allowed" as const, ...over,
  });

  it("reads windowSeconds, null when missing or not positive", () => {
    const read = (windowSeconds: unknown) =>
      toUsageLimits({ enabled: true, providers: [{ provider: "x", windows: [{ window: "w", windowSeconds }] }] })?.providers[0].windows[0].windowSeconds;
    expect(read(18000)).toBe(18000);
    expect(read(undefined)).toBeNull();
    expect(read(0)).toBeNull();
    expect(read(-5)).toBeNull();
  });

  it("expectedShare is the share of the window gone, clamped, null without a length", () => {
    expect(expectedShare(win(0.5, 5), now)).toBeCloseTo(0.5);
    expect(expectedShare(win(0.5, 20), now)).toBe(0);
    expect(expectedShare(win(0.5, -1), now)).toBe(1);
    expect(expectedShare(win(0.5, 5, { windowSeconds: null }), now)).toBeNull();
    expect(expectedShare(win(0.5, 5, { windowSeconds: undefined }), now)).toBeNull();
  });

  it("notes a window that will run out well before its reset", () => {
    // 4 h gone (0.4), 80% used: 1 h to burn the last 20%, 6 h left => 5 h ahead.
    const note = paceNote(win(0.8, 6), now);
    expect(note?.aheadBySeconds).toBeCloseTo(5 * 3600);
    expect(note?.text).toBe("Ahead of pace: at this rate it runs out about 5 h before it resets");
  });

  it("stays quiet when not over pace, at pace, empty, full, or without a length", () => {
    expect(paceNote(win(0.3, 6), now)).toBeNull();
    expect(paceNote(win(0.4, 6), now)).toBeNull();
    expect(paceNote(win(0, 6), now)).toBeNull();
    expect(paceNote(win(1, 6), now)).toBeNull();
    expect(paceNote(win(0.8, 6, { windowSeconds: null }), now)).toBeNull();
    expect(paceNote(win(null, 6), now)).toBeNull();
  });

  it("stays quiet under 5% elapsed, after the reset, and when it runs out less than 15 min early", () => {
    expect(paceNote(win(0.5, 9.7), now)).toBeNull();
    expect(paceNote(win(0.5, -1), now)).toBeNull();
    // 5 h gone, 51% used: projected 4.8 h, 5 h left => 0.2 h early.
    expect(paceNote(win(0.51, 5), now)).toBeNull();
  });

  it("formats durations", () => {
    expect(formatDuration(45 * 60)).toBe("45 min");
    expect(formatDuration(2 * 3600)).toBe("2 h");
    expect(formatDuration(27 * 3600)).toBe("1 d 3 h");
    expect(formatDuration(48 * 3600)).toBe("2 d");
  });
});
