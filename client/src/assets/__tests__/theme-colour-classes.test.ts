// Every colour class the client writes must produce CSS. Tailwind v4 makes a colour utility only from a
// `--color-*` entry in main.css's `@theme`, and silently makes nothing for a name it doesn't know: the shared
// ui/ components once wrote text-muted-foreground, bg-secondary, bg-input and friends with no token behind
// them, so muted text rendered at full colour and the Switch had no off track. This compiles main.css with
// Tailwind and asks it for each colour class found in the source.
import { readFileSync, readdirSync } from "node:fs";
import { join, resolve } from "node:path";
import { compile } from "tailwindcss";
import { describe, expect, it } from "vitest";

const clientDir = resolve(__dirname, "../../..");
const srcDir = join(clientDir, "src");

// Words in the source that look like colour classes but aren't classes (SVG attributes, data, prose).
const NOT_CLASSES = new Set(["from-you", "text-range", "stroke-width", "text-anchor", "text-less"]);

// A colour-family utility (with or without variants such as hover: or data-[state=on]:), whole word in a
// string or template. CSS properties (`text-align:`) are skipped by the lookahead; <style> blocks are dropped.
const COLOUR_CLASS =
  /(?<=^|[\s"'`:])(?:bg|text|border(?:-[xytrbl])?|ring(?:-offset)?|outline|divide|fill|stroke|from|via|to|placeholder|caret|decoration|accent)-[a-z][a-z0-9-]*(?:\/\d+)?(?=$|[\s"'`,\]})])/gm;

function colourClassesInSource(): Map<string, string> {
  const found = new Map<string, string>();
  const files = readdirSync(srcDir, { recursive: true, encoding: "utf8" }).filter(
    (f) => /\.(vue|ts)$/.test(f) && !/__tests__|\.test\.ts$|\.d\.ts$/.test(f),
  );
  for (const file of files) {
    const text = readFileSync(join(srcDir, file), "utf8").replace(/<style[\s\S]*?<\/style>/g, "");
    for (const m of text.matchAll(COLOUR_CLASS)) if (!found.has(m[0])) found.set(m[0], file);
  }
  return found;
}

async function compileTheme() {
  // Only Tailwind itself is needed: the other imports are plain stylesheets with no @theme.
  const css = readFileSync(join(srcDir, "assets/main.css"), "utf8").replace(/^@import "(?!tailwindcss").*$/gm, "");
  return compile(css, {
    base: clientDir,
    loadStylesheet: async (_id: string, base: string) => ({
      path: "tailwindcss/index.css",
      base,
      content: readFileSync(join(clientDir, "node_modules/tailwindcss/index.css"), "utf8"),
    }),
  });
}

describe("theme colour classes", () => {
  it("finds the colour classes it checks", () => {
    const found = colourClassesInSource();
    expect(found.size).toBeGreaterThan(100);
    expect(found.has("text-muted-foreground")).toBe(true);
  });

  it("every colour class in the source produces CSS", async () => {
    const compiler = await compileTheme();
    // build() is cumulative: a class that adds nothing to the stylesheet is one Tailwind doesn't know.
    let css = compiler.build([]);
    const missing: string[] = [];
    for (const [cls, file] of colourClassesInSource()) {
      if (NOT_CLASSES.has(cls)) continue;
      const next = compiler.build([cls]);
      if (next.length === css.length) missing.push(`${cls} (src/${file})`);
      css = next;
    }
    expect(missing, "add a --color-* token to @theme in main.css, or use an existing one").toEqual([]);
  });

  it.each([
    // `@theme inline` writes the Fleet variable straight into the rule.
    ["text-muted-foreground", "color: var(--muted)"],
    ["bg-secondary", "background-color: color-mix(in srgb, var(--text) 8%, transparent)"],
    ["bg-input", "background-color: color-mix(in srgb, var(--text) 18%, transparent)"],
    ["text-accent-foreground", "color: var(--primary-foreground)"],
  ])("%s maps to a Fleet token", async (cls, rule) => {
    const compiler = await compileTheme();
    expect(compiler.build([cls])).toContain(rule);
  });
});
