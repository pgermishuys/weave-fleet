import { describe, expect, it } from "vitest";
import {
  contextPercent,
  contextTone,
  formatTokens,
  toContextUsage,
  turnsBeforeCompaction,
  type SessionContextUsage,
} from "@/lib/context-usage";

function context(overrides: Partial<SessionContextUsage> = {}): SessionContextUsage {
  return {
    sessionId: "s1",
    used: 50_000,
    limit: 200_000,
    compactsAt: 167_000,
    modelId: "claude-opus-5",
    providerId: "anthropic",
    lastCall: null,
    lastCallAt: null,
    compacting: false,
    compactedAt: null,
    compactionError: null,
    turns: [],
    updatedAt: "2026-10-06T10:00:00Z",
    ...overrides,
  };
}

function turns(...used: number[]) {
  return used.map((value, index) => ({ used: value, limit: 200_000, at: `2026-10-06T10:0${index}:00Z`, afterCompaction: false }));
}

describe("toContextUsage", () => {
  it("reads Fleet's payload, with what the server left out as null", () => {
    const usage = toContextUsage({
      sessionId: "s1",
      used: 76000,
      limit: 200000,
      lastCall: { input: 1000, cacheRead: 74500, cacheWrite: 0, output: 500, reasoning: 0, used: 76000 },
      compacting: false,
      turns: [{ used: 76000, limit: 200000, at: "2026-10-06T10:00:00Z", afterCompaction: false }, { bad: true }],
      updatedAt: "2026-10-06T10:00:00Z",
    });

    expect(usage).toMatchObject({ used: 76_000, limit: 200_000, compactsAt: null, modelId: null, compactedAt: null });
    expect(usage?.lastCall?.cacheRead).toBe(74_500);
    expect(usage?.turns).toHaveLength(1);
  });

  it("isn't a context without a session", () => {
    expect(toContextUsage(null)).toBeNull();
    expect(toContextUsage({ used: 1 })).toBeNull();
  });

  it("reads counts the API sends as strings, and a missing window as unknown", () => {
    const usage = toContextUsage({ sessionId: "s1", used: "17148", limit: 0, turns: [] });
    expect(usage?.used).toBe(17_148);
    expect(usage?.limit).toBeNull();
  });
});

describe("contextPercent and contextTone", () => {
  it("is the size against the window, quiet until three-quarters full", () => {
    expect(contextPercent(context({ used: 76_000 }))).toBe(38);
    expect(contextTone(38)).toBe("ok");
    expect(contextTone(75)).toBe("warn");
    expect(contextTone(90)).toBe("danger");
  });

  it("is unknown without a size or a window, and never over 100", () => {
    expect(contextPercent(context({ used: null }))).toBeNull();
    expect(contextPercent(context({ limit: null }))).toBeNull();
    expect(contextPercent(context({ used: 250_000 }))).toBe(100);
    expect(contextTone(null)).toBe("ok");
  });
});

describe("turnsBeforeCompaction", () => {
  it("is how many more turns fit at the recent pace", () => {
    expect(turnsBeforeCompaction(context({ used: 50_000, turns: turns(20_000, 30_000, 40_000, 50_000) }))).toBe(11);
  });

  it("counts only the turns since the last compaction", () => {
    const history = [...turns(150_000, 160_000), { used: 20_000, limit: 200_000, at: "2026-10-06T11:00:00Z", afterCompaction: true }, ...turns(25_000)];
    expect(turnsBeforeCompaction(context({ used: 25_000, turns: history }))).toBe(28);
  });

  it("can't tell without a compaction point, enough turns, or growth", () => {
    expect(turnsBeforeCompaction(context({ compactsAt: null, turns: turns(1, 2, 3) }))).toBeNull();
    expect(turnsBeforeCompaction(context({ turns: turns(50_000) }))).toBeNull();
    expect(turnsBeforeCompaction(context({ turns: turns(50_000, 40_000) }))).toBeNull();
  });
});

describe("formatTokens", () => {
  it("writes counts the way the meter shows them", () => {
    expect(formatTokens(950)).toBe("950");
    expect(formatTokens(17_148)).toBe("17.1k");
    expect(formatTokens(200_000)).toBe("200k");
    expect(formatTokens(1_000_000)).toBe("1M");
    expect(formatTokens(1_500_000)).toBe("1.5M");
  });
});
