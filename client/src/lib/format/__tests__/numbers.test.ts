import { describe, expect, it } from "vitest";
import { formatBytes, formatCount, formatMoney, formatTokens } from "../numbers";

const us = { locale: "en-US" };
const de = { locale: "de-DE" };

describe("formatCount", () => {
  it("groups by the locale and rounds to a whole number", () => {
    expect(formatCount(0, us)).toBe("0");
    expect(formatCount(12_500, us)).toBe("12,500");
    expect(formatCount(12_500, de)).toBe("12.500");
    expect(formatCount(2.6, us)).toBe("3");
  });

  it("is empty for no number", () => {
    expect(formatCount(Number.NaN)).toBe("");
    expect(formatCount(null)).toBe("");
    expect(formatCount(Number.POSITIVE_INFINITY)).toBe("");
  });
});

describe("formatTokens", () => {
  it.each([
    [0, "0"],
    [950, "950"],
    [999, "999"],
    [999.6, "1k"],
    [1000, "1k"],
    [1_200, "1.2k"],
    [17_100, "17.1k"],
    [99_949, "99.9k"],
    [99_950, "100k"],
    [200_000, "200k"],
    [999_499, "999k"],
    [999_500, "1M"],
    [1_000_000, "1M"],
    [3_400_000, "3.4M"],
    [125_000_000, "125M"],
    [999_500_000, "1B"],
    [3_400_000_000, "3.4B"],
    [-1_200, "-1.2k"],
  ])("writes %d as %s", (n, expected) => {
    expect(formatTokens(n, us)).toBe(expected);
  });

  it("uses the locale's decimal mark", () => {
    expect(formatTokens(17_100, de)).toBe("17,1k");
  });

  it("is empty for no number", () => {
    expect(formatTokens(Number.NaN)).toBe("");
    expect(formatTokens(undefined)).toBe("");
  });
});

describe("formatMoney", () => {
  it("has two decimals and the narrow symbol", () => {
    expect(formatMoney(0, us)).toBe("$0.00");
    expect(formatMoney(1.5, us)).toBe("$1.50");
    expect(formatMoney(1234.5, us)).toBe("$1,234.50");
    expect(formatMoney(1.5, { locale: "en-GB" })).toBe("$1.50");
  });

  it("says <$0.01 for an amount that would round to nothing, and a cent from half a cent", () => {
    expect(formatMoney(0.004, us)).toBe("<$0.01");
    expect(formatMoney(0.0001, us)).toBe("<$0.01");
    expect(formatMoney(0.005, us)).toBe("$0.01");
  });

  it("takes the currency from the data", () => {
    expect(formatMoney(2, { ...us, currency: "EUR" })).toBe("€2.00");
  });

  it("is empty for no amount", () => {
    expect(formatMoney(Number.NaN)).toBe("");
    expect(formatMoney(null)).toBe("");
  });
});

describe("formatBytes", () => {
  it.each([
    [0, "0 B"],
    [812, "812 B"],
    [1024, "1 KB"],
    [34 * 1024, "34 KB"],
    [1024 ** 2, "1.0 MB"],
    [3.4 * 1024 ** 2, "3.4 MB"],
    [340 * 1024 ** 2, "340 MB"],
    [2.1 * 1024 ** 3, "2.1 GB"],
  ])("writes %d as %s", (n, expected) => {
    expect(formatBytes(n, us)).toBe(expected);
  });

  it("is empty for no size", () => {
    expect(formatBytes(Number.NaN)).toBe("");
  });
});
