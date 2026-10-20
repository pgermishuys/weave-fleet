import { describe, expect, it } from "vitest";
import { providerWindowLabel, tightestWindow, toUsageLimits, usageTone, windowPercent } from "@/lib/usage-providers";

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
    expect(limits?.providers[0].windows).toEqual([{ window: "primary", utilization: 1, resetsAt: null, status: "allowed" }]);
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
