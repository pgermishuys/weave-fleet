import { describe, expect, it } from "vitest";
import { codeThemes, isCodeThemeAvailable, resolveCodePalette, syntaxTokens } from "@/lib/code-themes";

describe("code themes", () => {
  it("offers Darcula, Monokai, GitHub, One Dark, Solarized and Nord", () => {
    expect(codeThemes.map((t) => t.id)).toEqual(["darcula", "monokai", "github", "one-dark", "solarized", "nord"]);
  });

  it("gives every scheme a dark palette with all eight tokens as hex colours", () => {
    for (const theme of codeThemes) {
      for (const token of syntaxTokens) {
        expect(theme.dark[token], `${theme.id} dark ${token}`).toMatch(/^#[0-9a-f]{6}$/);
      }
      if (theme.light) {
        for (const token of syntaxTokens) {
          expect(theme.light[token], `${theme.id} light ${token}`).toMatch(/^#[0-9a-f]{6}$/);
        }
      }
    }
  });

  it("'match' picks the scheme named after the app theme", () => {
    expect(resolveCodePalette("match", "nord", "dark")).toBe(codeThemes.find((t) => t.id === "nord")!.dark);
    expect(resolveCodePalette("match", "monokai", "dark")).toBe(codeThemes.find((t) => t.id === "monokai")!.dark);
    expect(resolveCodePalette("match", "github-dark", "dark")).toBe(codeThemes.find((t) => t.id === "github")!.dark);
    expect(resolveCodePalette("match", "solarized-light", "light")).toBe(codeThemes.find((t) => t.id === "solarized")!.light);
  });

  it("'match' falls back to One Dark on a dark theme and One Light on a light theme", () => {
    const oneDark = codeThemes.find((t) => t.id === "one-dark")!;
    expect(resolveCodePalette("match", "dark", "dark")).toBe(oneDark.dark);
    expect(resolveCodePalette("match", "dracula", "dark")).toBe(oneDark.dark);
    expect(resolveCodePalette("match", "light", "light")).toBe(oneDark.light);
  });

  it("an explicit scheme uses its light palette on a light theme when it has one", () => {
    const github = codeThemes.find((t) => t.id === "github")!;
    expect(resolveCodePalette("github", "light", "light")).toBe(github.light);
    expect(resolveCodePalette("github", "nord", "dark")).toBe(github.dark);
  });

  it("Darcula, Monokai and Nord are for dark themes only", () => {
    expect(codeThemes.filter((t) => !isCodeThemeAvailable(t.id, "light")).map((t) => t.id)).toEqual(["darcula", "monokai", "nord"]);
    expect(codeThemes.every((t) => isCodeThemeAvailable(t.id, "dark"))).toBe(true);
  });

  it("a dark-only scheme falls back to Match theme on a light theme", () => {
    const oneDark = codeThemes.find((t) => t.id === "one-dark")!;
    const solarized = codeThemes.find((t) => t.id === "solarized")!;
    expect(resolveCodePalette("darcula", "light", "light")).toBe(oneDark.light);
    expect(resolveCodePalette("monokai", "solarized-light", "light")).toBe(solarized.light);
    expect(resolveCodePalette("nord", "dark", "dark")).toBe(codeThemes.find((t) => t.id === "nord")!.dark);
  });
});
