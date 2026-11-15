import { describe, expect, it } from "vitest";
import { formatDate, formatDateTime, formatRelative, formatTime, formatTimestamp, formatWhen, toTimestamp } from "../time";

// A fixed clock: Saturday 14 November 2026, 12:00 UTC. Every call pins the zone and the locale.
const NOW = Date.UTC(2026, 10, 14, 12, 0, 0);
const S = 1000;
const M = 60 * S;
const H = 60 * M;
const D = 24 * H;
const W = 7 * D;
const gb = { locale: "en-GB", timeZone: "UTC" };
const us = { locale: "en-US", timeZone: "UTC" };

const compact = (agoMs: number) => formatRelative(NOW - agoMs, { ...gb, now: NOW, compact: true });
const words = (agoMs: number) => formatRelative(NOW - agoMs, { ...gb, now: NOW });

describe("toTimestamp", () => {
  it("reads unix ms, Dates, ISO and SQLite strings", () => {
    expect(toTimestamp(NOW)).toBe(NOW);
    expect(toTimestamp(new Date(NOW))).toBe(NOW);
    expect(toTimestamp("2026-11-14T12:00:00Z")).toBe(NOW);
    expect(toTimestamp("2026-11-14T14:00:00+02:00")).toBe(NOW);
    expect(toTimestamp(String(NOW))).toBe(NOW);
  });

  it("treats a string without a zone as UTC, the way SQLite writes it", () => {
    expect(toTimestamp("2026-11-14 12:00:00")).toBe(NOW);
    expect(toTimestamp("2026-11-14T12:00:00")).toBe(NOW);
  });

  it("reads a bare date as that day's local midnight", () => {
    expect(toTimestamp("2026-11-14")).toBe(new Date(2026, 10, 14).getTime());
  });

  it("gives NaN for nothing and for nonsense", () => {
    for (const bad of [null, undefined, "", "soon", 0, -5, Number.NaN, Number.POSITIVE_INFINITY, new Date("x")]) {
      expect(toTimestamp(bad)).toBeNaN();
    }
  });
});

describe("formatRelative, compact", () => {
  it("is now for the first minute", () => {
    expect(compact(0)).toBe("now");
    expect(compact(59 * S)).toBe("now");
  });

  it("counts minutes from 60 seconds, rounding down", () => {
    expect(compact(60 * S)).toBe("1m");
    expect(compact(59 * M + 59 * S)).toBe("59m");
  });

  it("counts hours from 60 minutes up to 23", () => {
    expect(compact(H)).toBe("1h");
    expect(compact(23 * H + 59 * M)).toBe("23h");
  });

  it("counts days up to 6, then weeks from 7 days", () => {
    expect(compact(D)).toBe("1d");
    expect(compact(6 * D + 23 * H)).toBe("6d");
    expect(compact(7 * D)).toBe("1w");
    expect(compact(8 * W - 1)).toBe("7w");
  });

  it("is a date from eight weeks, never 119w", () => {
    expect(compact(8 * W)).toBe("19 Sept");
    expect(compact(119 * W)).toBe("3 Aug 2024");
  });

  it("writes the year once the date is in another year", () => {
    // Past eight weeks on a clock in the new year: the date carries last year.
    const newYear = Date.UTC(2027, 0, 2, 12);
    expect(formatRelative(Date.UTC(2026, 10, 1), { ...gb, now: newYear, compact: true })).toBe("1 Nov 2026");
    expect(formatRelative(Date.UTC(2027, 0, 1, 12), { ...gb, now: newYear, compact: true })).toBe("1d");
  });

  it("orders day and month by the locale", () => {
    expect(formatRelative(NOW - 10 * W, { ...us, now: NOW, compact: true })).toBe("Sep 5");
  });

  it("reads a future time as in …, and a few seconds ahead as now", () => {
    expect(compact(-30 * S)).toBe("now");
    expect(compact(-5 * M)).toBe("in 5m");
    expect(compact(-3 * D)).toBe("in 3d");
  });

  it("is empty for invalid input", () => {
    expect(formatRelative(null, { now: NOW, compact: true })).toBe("");
    expect(formatRelative("not a time", { now: NOW, compact: true })).toBe("");
  });
});

describe("formatRelative, words", () => {
  it("says just now, then minutes, hours, days and weeks in words", () => {
    expect(words(59 * S)).toBe("just now");
    expect(words(60 * S)).toBe("1 minute ago");
    expect(words(2 * M)).toBe("2 minutes ago");
    expect(words(23 * H)).toBe("23 hours ago");
    expect(words(D)).toBe("yesterday");
    expect(words(3 * D)).toBe("3 days ago");
    expect(words(W)).toBe("last week");
    expect(words(3 * W)).toBe("3 weeks ago");
  });

  it("is the date from eight weeks", () => {
    expect(words(8 * W)).toBe("19 Sept");
  });

  it("says in … for the future", () => {
    expect(words(-2 * H)).toBe("in 2 hours");
  });
});

describe("absolute times", () => {
  const at = Date.UTC(2026, 9, 12, 14, 7, 32);

  it("writes the clock by the locale, 24 or 12 hour", () => {
    expect(formatTime(at, gb)).toBe("14:07");
    expect(formatTime(at, us)).toBe("2:07 PM");
    expect(formatTime(at, { ...gb, seconds: true })).toBe("14:07:32");
    expect(formatTime(Date.UTC(2026, 9, 12, 9, 5), gb)).toBe("09:05");
    expect(formatTime(Date.UTC(2026, 9, 12, 9, 5), us)).toBe("9:05 AM");
  });

  it("leaves the year out this year and writes it otherwise", () => {
    expect(formatDate(at, { ...gb, now: NOW })).toBe("12 Oct");
    expect(formatDate(at, { ...us, now: NOW })).toBe("Oct 12");
    expect(formatDate(at, { ...gb, now: Date.UTC(2027, 0, 1) })).toBe("12 Oct 2026");
    expect(formatDate(at, { ...gb, now: NOW, weekday: true })).toBe("Mon 12 Oct");
  });

  it("joins the day and the time", () => {
    expect(formatDateTime(at, { ...gb, now: NOW })).toBe("12 Oct, 14:07");
    expect(formatDateTime(at, { ...us, now: NOW })).toBe("Oct 12, 2:07 PM");
    expect(formatDateTime(at, { ...gb, now: Date.UTC(2027, 5, 1) })).toBe("12 Oct 2026, 14:07");
  });

  it("stamps today's times with the clock alone", () => {
    expect(formatTimestamp(NOW - 2 * H, { ...gb, now: NOW })).toBe("10:00");
    expect(formatTimestamp(at, { ...gb, now: NOW })).toBe("12 Oct, 14:07");
  });

  it("counts today by the time zone, not UTC", () => {
    // 23:30 UTC on the 13th is already the 14th in Johannesburg.
    const late = Date.UTC(2026, 10, 13, 23, 30);
    expect(formatTimestamp(late, { locale: "en-GB", timeZone: "Africa/Johannesburg", now: NOW })).toBe("01:30");
    expect(formatTimestamp(late, { ...gb, now: NOW })).toBe("13 Nov, 23:30");
  });

  it("is empty for invalid input", () => {
    expect(formatTime(undefined)).toBe("");
    expect(formatDate("nope")).toBe("");
    expect(formatDateTime(0)).toBe("");
    expect(formatTimestamp(Number.NaN)).toBe("");
  });
});

describe("without Intl.RelativeTimeFormat (the phone app's engine)", () => {
  it("still says it in words", () => {
    const original = Intl.RelativeTimeFormat;
    const intl = Intl as { RelativeTimeFormat?: typeof Intl.RelativeTimeFormat };
    intl.RelativeTimeFormat = undefined;
    try {
      expect(words(2 * M)).toBe("2 minutes ago");
      expect(words(H)).toBe("1 hour ago");
      expect(words(D)).toBe("yesterday");
      expect(words(W)).toBe("last week");
      expect(words(-3 * D)).toBe("in 3 days");
    } finally {
      intl.RelativeTimeFormat = original;
    }
  });
});

describe("formatWhen, a time near now", () => {
  it("is the clock today, the weekday within six days, and the date after", () => {
    expect(formatWhen(NOW + 2 * H, { ...gb, now: NOW })).toBe("14:00");
    expect(formatWhen(NOW + 2 * D + 2 * H, { ...gb, now: NOW })).toBe("Mon 14:00");
    expect(formatWhen(NOW - 2 * D, { ...gb, now: NOW })).toBe("Thu 12:00");
    expect(formatWhen(NOW + 9 * D, { ...gb, now: NOW })).toBe("23 Nov, 12:00");
    expect(formatWhen(NOW + 2 * D + 2 * H, { ...us, now: NOW })).toBe("Mon 2:00 PM");
    expect(formatWhen(null, { now: NOW })).toBe("");
  });
});
