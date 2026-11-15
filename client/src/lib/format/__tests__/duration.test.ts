import { describe, expect, it } from "vitest";
import { formatCountdown, formatDuration, formatElapsedSince } from "../duration";

const S = 1000;
const M = 60 * S;
const H = 60 * M;
const D = 24 * H;

describe("formatDuration", () => {
  it("counts seconds under a minute", () => {
    expect(formatDuration(0)).toBe("0 s");
    expect(formatDuration(45 * S)).toBe("45 s");
    expect(formatDuration(59 * S + 999)).toBe("59 s");
  });

  it("writes minutes and padded seconds from 60 seconds", () => {
    expect(formatDuration(60 * S)).toBe("1 min");
    expect(formatDuration(3 * M + 20 * S)).toBe("3 min 20 s");
    expect(formatDuration(3 * M + 5 * S)).toBe("3 min 05 s");
    expect(formatDuration(59 * M + 59 * S)).toBe("59 min 59 s");
  });

  it("writes hours and padded minutes from an hour, without seconds", () => {
    expect(formatDuration(H)).toBe("1 h");
    expect(formatDuration(H + 5 * M + 30 * S)).toBe("1 h 05 min");
    expect(formatDuration(23 * H + 59 * M)).toBe("23 h 59 min");
  });

  it("writes days and padded hours from a day", () => {
    expect(formatDuration(D)).toBe("1 d");
    expect(formatDuration(13 * D + 6 * H)).toBe("13 d 06 h");
  });

  it("drops seconds past the first minute at minutes precision", () => {
    expect(formatDuration(58 * S, { precision: "minutes" })).toBe("58 s");
    expect(formatDuration(4 * M + 59 * S, { precision: "minutes" })).toBe("4 min");
  });

  it("shows tenths under ten seconds when asked, never less than 0.1 s", () => {
    expect(formatDuration(3, { tenths: true })).toBe("0.1 s");
    expect(formatDuration(450, { tenths: true })).toBe("0.4 s");
    expect(formatDuration(3_250, { tenths: true })).toBe("3.2 s");
    expect(formatDuration(10 * S, { tenths: true })).toBe("10 s");
  });

  it("is 0 s for a negative span and empty for no span", () => {
    expect(formatDuration(-5 * S)).toBe("0 s");
    expect(formatDuration(Number.NaN)).toBe("");
    expect(formatDuration(Number.POSITIVE_INFINITY)).toBe("");
    expect(formatDuration(undefined)).toBe("");
    expect(formatDuration(null)).toBe("");
  });
});

describe("formatElapsedSince", () => {
  it("measures from a start to now", () => {
    expect(formatElapsedSince(1_000_000, 1_000_000 + 72 * S)).toBe("1 min 12 s");
  });

  it("is empty with no start", () => {
    expect(formatElapsedSince(null, 5)).toBe("");
    expect(formatElapsedSince(0, 5)).toBe("");
  });
});

describe("formatCountdown", () => {
  it.each([
    [40 * S, "in 40 s"],
    [58 * S + 1, "in 59 s"],
    [59 * S + 500, "in 1 min"],
    [12 * M, "in 12 min"],
    [11 * M + 1, "in 12 min"],
    [120 * M, "in 2 h"],
    [125 * M, "in 2 h 05 min"],
    [3 * D, "in 3 d"],
  ])("puts %i ms as %s, rounding up", (left, expected) => {
    expect(formatCountdown(left)).toBe(expected);
  });

  it("is now once it's due, and empty with no time", () => {
    expect(formatCountdown(0)).toBe("now");
    expect(formatCountdown(-1)).toBe("now");
    expect(formatCountdown(Number.NaN)).toBe("");
    expect(formatCountdown(Number.POSITIVE_INFINITY)).toBe("");
  });
});
