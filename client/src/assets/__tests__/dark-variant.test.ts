// Tailwind's dark: must follow Fleet's theme, not the operating system's colour scheme. Out of the box it compiles to
// @media (prefers-color-scheme: dark), so with a dark Fleet theme on a light OS the shared components lost their dark
// styles (the Switch thumb, field fills), and with a light Fleet theme on a dark OS they took them. main.css ties the
// variant to <html data-theme>; this compiles it with Tailwind and checks it against every theme Fleet ships.
import { readFileSync } from "node:fs";
import { join, resolve } from "node:path";
import { compile } from "tailwindcss";
import { afterEach, describe, expect, it } from "vitest";
import { themes } from "@/stores/theme";

const clientDir = resolve(__dirname, "../../..");

async function darkRule(cls: string): Promise<string> {
  const css = readFileSync(join(clientDir, "src/assets/main.css"), "utf8").replace(/^@import "(?!tailwindcss").*$/gm, "");
  const compiler = await compile(css, {
    base: clientDir,
    loadStylesheet: async (_id: string, base: string) => ({
      path: "tailwindcss/index.css",
      base,
      content: readFileSync(join(clientDir, "node_modules/tailwindcss/index.css"), "utf8"),
    }),
  });
  const out = compiler.build([cls]);
  return out.slice(out.indexOf(".dark\\:"));
}

/** The condition the variant puts on an element, e.g. `:where(:root:not([data-theme="light"]) *)`. */
function condition(rule: string): string {
  const m = rule.match(/&(:where\(.*\))\s*\{/);
  expect(m, `a selector condition in:\n${rule}`).not.toBeNull();
  return m![1];
}

describe("the dark: variant", () => {
  afterEach(() => {
    delete document.documentElement.dataset.theme;
    document.body.innerHTML = "";
  });

  it("doesn't follow the operating system's colour scheme", async () => {
    const rule = await darkRule("dark:bg-input/30");
    expect(rule).not.toContain("prefers-color-scheme");
  });

  it.each(themes.map((t) => [t.id, t.colorScheme] as const))(
    "applies under the %s theme only if it is dark (%s), whatever the OS says",
    async (id, colorScheme) => {
      const where = condition(await darkRule("dark:bg-input/30"));
      document.documentElement.dataset.theme = id;
      const el = document.createElement("span");
      document.body.appendChild(el);
      expect(el.matches(where)).toBe(colorScheme === "dark");
    },
  );

  it("applies before the theme store has set a theme (main.css's defaults are the dark theme)", async () => {
    const where = condition(await darkRule("dark:bg-input/30"));
    const el = document.createElement("span");
    document.body.appendChild(el);
    expect(el.matches(where)).toBe(true);
  });
});
