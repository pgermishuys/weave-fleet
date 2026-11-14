// Contrast every shipped theme must keep (WCAG 2.1 AA), checked on main.css itself so a new theme can't miss it:
// - Keyboard focus: a 2px ring in --focus-ring on every focusable element, 3:1 against every surface (1.4.11). The old
//   global rule was `outline: none !important` in @layer base: 27 of 45 tab stops on a session showed nothing.
// - Text on the accent (primary buttons, highlighted rows): --primary-foreground on --accent, 4.5:1 (1.4.3). White on
//   the default #6366f1 was 4.46:1, and components wrote text-white on the accent instead of the theme's foreground.
// - Age-dimmed session titles: a quiet session's row keeps --row-dim-1 / --row-dim-2 of --text, 4.5:1 on the surfaces
//   the session list sits on (1.4.3). A fixed 40% was 3.34:1 in the default theme and 2.26:1 in solarized-light.
// - Every other text in a session row (its age, labels, status, PR number): --row-meta of --text, or a status colour
//   that keeps --row-<colour> of itself and takes the rest from --text, 4.5:1 on every background a row can have
//   (resting, hovered, open, picked, and a chip's own tint). A quiet row's age was --muted at 60%: 2.24:1 in dark,
//   and the open row's age 3.47:1 on its highlight.
import { readFileSync, readdirSync } from "node:fs";
import { join, resolve } from "node:path";
import { describe, expect, it } from "vitest";
import prBadgeSource from "../../components/github/PrBadge.vue?raw";
import sessionItemSource from "../../components/sessions/SessionItem.vue?raw";

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

/** `text` at `percent` over `bg`, as the browser draws color-mix(in srgb, text p%, transparent) on that surface. */
function over(text: string, percent: number, bg: string): string {
  const channels = (hex: string) => [0, 2, 4].map((i) => parseInt(hex.replace("#", "").slice(i, i + 2), 16));
  const [t, b] = [channels(text), channels(bg)];
  return `#${t.map((c, i) => Math.round(c * percent / 100 + b[i] * (1 - percent / 100)).toString(16).padStart(2, "0")).join("")}`;
}

describe("age-dimmed session titles", () => {
  it.each(Object.entries(themes()))("reach 4.5:1 at both dim levels in the %s theme, and still dim", (_name, vars) => {
    const text = resolveVar(vars, vars.text);
    const [dim1, dim2] = [parseFloat(vars["row-dim-1"]), parseFloat(vars["row-dim-2"])];
    expect(dim2, "the deeper level dims more").toBeLessThan(dim1);
    expect(dim1, "dimming is kept").toBeLessThan(100);
    for (const surface of ["main-bg", "panel-bg"]) {
      const bg = resolveVar(vars, vars[surface]);
      for (const [level, percent] of [["--row-dim-1", dim1], ["--row-dim-2", dim2]] as const) {
        const shown = over(text, percent, bg);
        expect(contrast(shown, bg), `${level} ${percent}% of ${text} on --${surface} ${bg} = ${shown}`).toBeGreaterThanOrEqual(4.5);
      }
    }
  });

  it("are dimmed by those tokens in SessionItem, not a fixed percentage", () => {
    const style = sessionItemSource.slice(sessionItemSource.indexOf("<style"));
    for (const level of [1, 2]) {
      const rule = new RegExp(`\\.session-item--dim-${level}[^{,]*\\{([^}]*)\\}`).exec(style)?.[1] ?? "";
      expect(rule).toMatch(new RegExp(`color:\\s*color-mix\\(in srgb, var\\(--text\\) var\\(--row-dim-${level}\\), transparent\\)`));
    }
  });
});

type Rgba = [number, number, number, number];

/** A theme value (hex, rgba() or a var() chain) as channels and alpha. */
function rgba(vars: Vars, value: string): Rgba {
  const v = resolveVar(vars, value).trim();
  const fn = v.match(/^rgba?\(([^)]+)\)$/);
  if (fn) {
    const [r, g, b, a = 1] = fn[1].split(/[\s,/]+/).filter(Boolean).map(Number);
    return [r, g, b, a];
  }
  const h = v.replace("#", "");
  return [...[0, 2, 4].map((i) => parseInt(h.slice(i, i + 2), 16)), 1] as Rgba;
}

const hex = (c: Rgba) => `#${c.slice(0, 3).map((x) => Math.round(x).toString(16).padStart(2, "0")).join("")}`;
/** `c` at `alpha` drawn on the opaque `bg`, rounded to whole channels as the browser does. */
const paint = (c: Rgba, alpha: number, bg: Rgba): Rgba =>
  [0, 1, 2].map((i) => Math.round(c[i] * c[3] * alpha + bg[i] * (1 - c[3] * alpha))).concat(1) as Rgba;
/** color-mix(in srgb, a p%, b) for opaque a and b. */
const mix = (a: Rgba, percent: number, b: Rgba): Rgba =>
  [0, 1, 2].map((i) => Math.round(a[i] * percent / 100 + b[i] * (1 - percent / 100))).concat(1) as Rgba;

/** Every `color:` a component's style sets, with its selector. */
function colours(source: string): { selector: string; value: string }[] {
  const style = source.slice(source.indexOf("<style"));
  const out: { selector: string; value: string }[] = [];
  for (const rule of style.matchAll(/([^{}]+)\{([^{}]*)\}/g)) {
    for (const decl of rule[2].matchAll(/(?<![-\w])color:\s*([^;]+);/g)) {
      out.push({ selector: rule[1].replace(/\/\*[\s\S]*?\*\//g, "").trim(), value: decl[1].trim() });
    }
  }
  return out;
}

const ROW_SOURCES = { "SessionItem.vue": sessionItemSource, "PrBadge.vue": prBadgeSource };
// Icons and the picker's tick (non-text, 3:1), not text.
const ROW_ICONS = /^\.session-(caret|row-action|check)\b/;
// Shares of --text a resting row's title takes; hover, the open row and a picked row restore --text itself.
const RESTING_ONLY = new Set(["86%", "var(--row-dim-1)", "var(--row-dim-2)"]);

describe("session row text", () => {
  const rowColours = Object.entries(ROW_SOURCES).flatMap(([file, src]) =>
    colours(src).filter((c) => !ROW_ICONS.test(c.selector)).map((c) => ({ file, ...c })),
  );
  const shareOfText = (value: string) => /^color-mix\(in srgb, var\(--text\) ([^,]+), transparent\)$/.exec(value)?.[1];
  const statusMix = (value: string) => /^color-mix\(in srgb, var\(--([a-z-]+)\) var\(--(row-[a-z]+)\), var\(--text\)\)$/.exec(value);

  it("uses only colours this test checks: --text, a share of it, or a status colour mixed with it", () => {
    const offenders = rowColours
      .filter(({ value }) => value !== "var(--text)" && !shareOfText(value) && !statusMix(value))
      .map(({ file, selector, value }) => `${file} ${selector}: ${value}`);
    expect(offenders).toEqual([]);
  });

  it("dims a title or an age only where the row rests: never hovered or picked", () => {
    const resting = rowColours.filter(({ value }) => RESTING_ONLY.has(shareOfText(value) ?? ""));
    expect(resting.map((c) => c.selector)).toEqual([
      ".session-item",
      ".session-item--dim-1:where(:not(.session-item--selected))",
      ".session-item--dim-2:where(:not(.session-item--selected))",
      ".session-item--dim-2:where(:not(:hover, .session-item--selected)) .session-meta--quiet",
    ]);
  });

  it("draws the row backgrounds this test measures", () => {
    const style = sessionItemSource.slice(sessionItemSource.indexOf("<style"));
    expect(style).toMatch(/\.session-item:hover \{[^}]*background: color-mix\(in srgb, var\(--text\) 5%, transparent\)/);
    expect(style).toMatch(/\.session-item\.active \{[^}]*background: color-mix\(in srgb, var\(--text\) 9%, transparent\)/);
    expect(style).toMatch(/\.session-item--selected\.active \{[^}]*background: var\(--accent-dim\)/);
  });

  it.each(Object.entries(themes()))("reaches 4.5:1 on every row background in the %s theme", (_name, vars) => {
    const text = rgba(vars, vars.text);
    const failures: string[] = [];
    for (const surface of ["main-bg", "panel-bg"]) {
      const resting = rgba(vars, vars[surface]);
      const backgrounds: Record<string, Rgba> = {
        resting,
        hovered: paint(text, 0.05, resting),
        open: paint(text, 0.09, resting),
        picked: paint(rgba(vars, vars["accent-dim"]), 1, resting),
      };
      for (const { file, selector, value } of rowColours) {
        const share = shareOfText(value);
        const status = statusMix(value);
        for (const [state, bg] of Object.entries(backgrounds)) {
          if (share && RESTING_ONLY.has(share) && state !== "resting") continue;
          // A status colour's chip sits on its own 14% tint.
          const grounds = status ? [bg, paint(rgba(vars, `var(--${status[1]})`), 0.14, bg)] : [bg];
          for (const ground of grounds) {
            const shown = value === "var(--text)"
              ? text
              : share
                ? paint(text, parseFloat(share.startsWith("var(") ? vars[share.slice(6, -1)] : share) / 100, ground)
                : mix(rgba(vars, `var(--${status![1]})`), parseFloat(vars[status![2]]), text);
            const ratio = contrast(hex(shown), hex(ground));
            if (ratio < 4.5) failures.push(`${file} ${selector} on a ${state} row (--${surface}): ${ratio.toFixed(2)}`);
          }
        }
      }
    }
    expect(failures).toEqual([]);
  });

  it.each(Object.entries(themes()))("keeps a quiet row's age quieter than its title in the %s theme", (_name, vars) => {
    const [meta, dim1, dim2] = ["row-meta", "row-dim-1", "row-dim-2"].map((k) => parseFloat(vars[k]));
    expect(meta, "--row-meta under a resting title's 86%").toBeLessThan(86);
    expect(dim2, "the quietest row's age fades with it").toBeLessThanOrEqual(meta);
    expect(dim2).toBeLessThan(dim1);
  });
});
