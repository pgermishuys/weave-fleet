import { afterEach, describe, expect, it } from "vitest";
import { CHART_SERIES, pageFrameName, pageThemeMessage, readPageTheme } from "@/lib/page-theme";

describe("readPageTheme", () => {
  afterEach(() => {
    document.documentElement.removeAttribute("style");
  });

  function themed(colorScheme: "dark" | "light", variables: Record<string, string>): HTMLElement {
    const root = document.documentElement;
    root.style.colorScheme = colorScheme;
    for (const [name, value] of Object.entries(variables)) root.style.setProperty(name, value);
    return root;
  }

  it("names the app's own colours and fonts as the page's variables", () => {
    const theme = readPageTheme(themed("dark", {
      "--panel-bg": "#141418",
      "--card-bg": "#1b1b20",
      "--text": "#e8e8ec",
      "--accent": "#6366f1",
      "--running": "#22c55e",
      "--font-sans-stack": "Inter, sans-serif",
    }));

    expect(theme.appearance).toBe("dark");
    expect(theme.variables).toMatchObject({
      "--fleet-bg": "#141418",
      "--fleet-surface": "#1b1b20",
      "--fleet-text": "#e8e8ec",
      "--fleet-accent": "#6366f1",
      "--fleet-success": "#22c55e",
      "--fleet-font-sans": "Inter, sans-serif",
    });
    // A colour the theme doesn't set isn't sent, so the page keeps Fleet's default for it.
    expect(theme.variables).not.toHaveProperty("--fleet-info");
  });

  it("gives a light theme the chart series for light grounds", () => {
    const light = readPageTheme(themed("light", { "--accent": "#5B6EC7" }));

    expect(light.appearance).toBe("light");
    expect(CHART_SERIES.light.map((_, index) => light.variables[`--fleet-chart-${index + 1}`])).toEqual(CHART_SERIES.light);
  });
});

describe("pageFrameName", () => {
  it("is fleet: and the place and theme as JSON, which the page's script reads", () => {
    const theme = { appearance: "light" as const, variables: { "--fleet-text": "#1A1918" } };
    const name = pageFrameName("conversation", theme);

    expect(name.startsWith("fleet:")).toBe(true);
    expect(JSON.parse(name.slice("fleet:".length))).toEqual({ place: "conversation", ...theme });
    expect(pageThemeMessage(theme)).toEqual({ type: "fleet:theme", ...theme });
  });
});
