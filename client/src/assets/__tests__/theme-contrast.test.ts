// Contrast every shipped theme must keep (WCAG 2.1 AA), checked on main.css itself so a new theme can't miss it:
// - Keyboard focus: a 2px ring in --focus-ring on every focusable element, 3:1 against every surface (1.4.11). The old
//   global rule was `outline: none !important` in @layer base: 27 of 45 tab stops on a session showed nothing.
// - Text on the accent (primary buttons, highlighted rows): --primary-foreground on --accent, 4.5:1 (1.4.3). White on
//   the default #6366f1 was 4.46:1, and components wrote text-white on the accent instead of the theme's foreground.
import { readFileSync, readdirSync } from "node:fs";
import { join, resolve } from "node:path";
import { describe, expect, it } from "vitest";

const css = readFileSync(resolve(__dirname, "../main.css"), "utf8");

function block(selector: string): string {
  const at = css.indexOf(`${selector} {`);
  expect(at, `${selector} in main.css`).toBeGreaterThanOrEqual(0);
  return css.slice(at, css.indexOf("\n  }", at));
}

type Vars = Record<string, string>;

function themes(): Record<string, Vars> {
  const out: Record<string, Vars> = {};
  for (const m of css.matchAll(/:root(?:\[data-theme="([^"]+)"\])?\s*\{([\s\S]*?)\n\}/g)) {
    const vars: Vars = {};
    for (const v of m[2].matchAll(/--([a-z0-9-]+):\s*([^;]+);/g)) vars[v[1]] = v[2].trim();
    out[m[1] ?? "dark"] = vars;
  }
  const base = out.dark;
  for (const name of Object.keys(out)) out[name] = { ...base, ...out[name] };
  return out;
}

function resolveVar(vars: Vars, value: string): string {
  const m = value.match(/^var\(--([a-z0-9-]+)\)$/);
  return m ? resolveVar(vars, vars[m[1]]) : value;
}

function luminance(hex: string): number {
  const h = hex.replace("#", "");
  const [r, g, b] = [0, 2, 4].map((i) => parseInt(h.slice(i, i + 2), 16) / 255);
  const f = (c: number) => (c <= 0.03928 ? c / 12.92 : ((c + 0.055) / 1.055) ** 2.4);
  return 0.2126 * f(r) + 0.7152 * f(g) + 0.0722 * f(b);
}

function contrast(a: string, b: string): number {
  const [x, y] = [luminance(a), luminance(b)].sort((p, q) => q - p);
  return (x + 0.05) / (y + 0.05);
}

describe("keyboard focus ring", () => {
  it("draws a 2px outline in --focus-ring on every :focus-visible element, and never removes it globally", () => {
    const rule = block("  *:focus-visible");
    expect(rule).toMatch(/outline:\s*2px solid var\(--focus-ring\)\s*!important/);
    expect(rule).not.toMatch(/outline:\s*none/);
  });

  it.each(Object.entries(themes()))("clears 3:1 against every surface in the %s theme", (_name, vars) => {
    const ring = resolveVar(vars, vars["focus-ring"]);
    for (const surface of ["panel-bg", "card-bg", "main-bg", "rail-bg"]) {
      const bg = resolveVar(vars, vars[surface]);
      expect(contrast(ring, bg), `${ring} on --${surface} ${bg}`).toBeGreaterThanOrEqual(3);
    }
  });
});

describe("text on the accent", () => {
  it.each(Object.entries(themes()))("reaches 4.5:1 in the %s theme", (_name, vars) => {
    const accent = resolveVar(vars, vars.accent);
    const text = resolveVar(vars, vars["primary-foreground"]);
    expect(contrast(text, accent), `${text} on ${accent}`).toBeGreaterThanOrEqual(4.5);
  });

  it("is the theme's --primary-foreground, never a hardcoded white", () => {
    const src = resolve(__dirname, "../..");
    const offenders: string[] = [];
    const files = readdirSync(src, { recursive: true, encoding: "utf8" }).filter(
      (f) => /\.(vue|ts|css)$/.test(f) && !/__tests__/.test(f),
    );
    for (const file of files) {
      const text = readFileSync(join(src, file), "utf8");
      text.split("\n").forEach((line, i) => {
        if (/\bbg-(accent|primary)\b/.test(line) && /(?<![:\w-])text-white\b/.test(line)) offenders.push(`${file}:${i + 1}`);
      });
      for (const rule of text.matchAll(/[^{}]+\{([^{}]*)\}/g)) {
        const body = rule[1];
        if (/background(-color)?:\s*var\(--(accent|primary)\)/.test(body) && /(?<![-\w])color:\s*(#fff\b|#ffffff\b|white\b)/i.test(body)) {
          offenders.push(`${file}: ${rule[0].trim().split("{")[0].trim()}`);
        }
      }
    }
    expect(offenders).toEqual([]);
  });
});
