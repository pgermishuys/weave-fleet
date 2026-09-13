import { describe, expect, it } from "vitest";
import { isLight, mix, terminalTheme, withAlpha } from "@/lib/terminal-theme";

function reader(values: Record<string, string>) {
  return (name: string) => values[name] ?? "";
}

describe("terminalTheme", () => {
  it("takes text, selection and status colours from the Fleet theme", () => {
    const theme = terminalTheme(reader({
      "--panel-bg": "#141418",
      "--text": "#e8e8ec",
      "--accent-dim": "rgba(99, 102, 241, 0.15)",
      "--error": "#ef4444",
      "--running": "#22c55e",
      "--idle": "#f59e0b",
      "--complete": "#3b82f6",
      "--queued": "#8b5cf6",
    }));

    expect(theme.foreground).toBe("#e8e8ec");
    expect(theme.cursor).toBe("#e8e8ec");
    expect(theme.cursorAccent).toBe("#141418");
    // 28% of the accent over the panel, opaque (xterm would blend a translucent one with black).
    expect(theme.selectionBackground).toBe(mix("#141418", "#6366f1", 0.28));
    expect(theme.selectionBackground).toMatch(/^#[0-9a-f]{6}$/);
    expect([theme.red, theme.green, theme.yellow, theme.blue, theme.magenta])
      .toEqual(["#ef4444", "#22c55e", "#f59e0b", "#3b82f6", "#8b5cf6"]);
    expect(theme.background).toBe("rgba(0, 0, 0, 0)");
    expect(theme.scrollbarSliderBackground).toBe("rgba(232, 232, 236, 0.16)");
  });

  it("uses a light palette for the colours a light theme doesn't set", () => {
    const light = terminalTheme(reader({ "--panel-bg": "#FFFFFF", "--text": "#1A1918" }));
    const dark = terminalTheme(reader({ "--panel-bg": "#0A0A0A", "--text": "#FAFAFA" }));

    expect(light.cyan).toBe("#0e7490");
    expect(dark.cyan).toBe("#67e8f9");
  });

  it("falls back when the theme sets nothing", () => {
    const theme = terminalTheme(() => "");

    expect(theme.foreground).toBeTruthy();
    expect(theme.red).toBeTruthy();
  });
});

describe("isLight", () => {
  it.each([
    ["#ffffff", true],
    ["#F3F2EF", true],
    ["#fff", true],
    ["rgb(250, 250, 250)", true],
    ["#141418", false],
    ["#1E293B", false],
    ["rgba(0, 0, 0, 0.5)", false],
    ["not a colour", false],
  ])("%s → %s", (color, expected) => {
    expect(isLight(color)).toBe(expected);
  });
});

describe("mix", () => {
  it("paints one colour over another, opaque", () => {
    expect(mix("#000000", "#ffffff", 0.5)).toBe("#808080");
    expect(mix("#ffffff", "#5b6ec7", 0.28)).toBe("#d1d6ef");
  });

  it("gives up on a colour it can't read", () => {
    expect(mix("var(--x)", "#fff", 0.5)).toBeNull();
  });
});

describe("withAlpha", () => {
  it("turns a hex colour into rgba", () => {
    expect(withAlpha("#1A1918", 0.28)).toBe("rgba(26, 25, 24, 0.28)");
  });

  it("leaves a colour it can't read alone", () => {
    expect(withAlpha("hsl(0 0% 50%)", 0.5)).toBe("hsl(0 0% 50%)");
  });
});
