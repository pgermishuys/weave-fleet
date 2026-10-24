// The pure parts of tokens.ts: parsing main.css, resolving var() and colour maths. No file access, so tests can import it.
export interface Rgba { r: number; g: number; b: number; a: number }
export type Vars = Record<string, string>;

/** The `--name: value;` declarations of the first rule whose selector is exactly `selector`. */
export function parseBlock(css: string, selector: string): Vars {
  const clean = css.replace(/\/\*[\s\S]*?\*\//g, "");
  const start = clean.split("\n").findIndex((line) => line.trim() === `${selector} {`);
  if (start < 0) throw new Error(`No "${selector}" rule in main.css`);
  const body = clean.split("\n").slice(start + 1);
  const vars: Vars = {};
  for (const line of body) {
    if (line.trim() === "}") break;
    const m = /^\s*(--[\w-]+)\s*:\s*(.+?);\s*$/.exec(line);
    if (m) vars[m[1]] = m[2];
  }
  return vars;
}

/** Follows var(--x) references until the value is a plain colour or length. */
export function resolveVar(vars: Vars, name: string, seen: string[] = []): string {
  if (seen.includes(name)) throw new Error(`Circular var(${name})`);
  const value = vars[name];
  if (value === undefined) throw new Error(`Unknown token ${name}`);
  return value.replace(/var\((--[\w-]+)\)/g, (_, ref: string) => resolveVar(vars, ref, [...seen, name]));
}

export function parseColor(value: string): Rgba {
  const hex = /^#([0-9a-f]{3}|[0-9a-f]{6})$/i.exec(value.trim());
  if (hex) {
    const h = hex[1].length === 3 ? [...hex[1]].map((c) => c + c).join("") : hex[1];
    return { r: parseInt(h.slice(0, 2), 16), g: parseInt(h.slice(2, 4), 16), b: parseInt(h.slice(4, 6), 16), a: 1 };
  }
  const rgb = /^rgba?\(\s*(\d+)\s*,\s*(\d+)\s*,\s*(\d+)\s*(?:,\s*([\d.]+)\s*)?\)$/.exec(value.trim());
  if (rgb) return { r: +rgb[1], g: +rgb[2], b: +rgb[3], a: rgb[4] === undefined ? 1 : +rgb[4] };
  throw new Error(`Not a colour: ${value}`);
}

/** color-mix(in srgb, top pct%, under): `under` null means transparent. Premultiplied, like CSS. */
export function mix(top: Rgba, pct: number, under: Rgba | null): Rgba {
  const wt = pct / 100;
  const wu = 1 - wt;
  const at = top.a * wt;
  const au = under ? under.a * wu : 0;
  const a = at + au;
  if (a === 0) return { r: 0, g: 0, b: 0, a: 0 };
  const ch = (k: "r" | "g" | "b") => (top[k] * at + (under ? under[k] * au : 0)) / a;
  return { r: ch("r"), g: ch("g"), b: ch("b"), a };
}

export function css(c: Rgba): string {
  const to = (n: number) => Math.round(n);
  if (c.a >= 0.9995) return "#" + [c.r, c.g, c.b].map((n) => to(n).toString(16).padStart(2, "0")).join("");
  return `rgba(${to(c.r)}, ${to(c.g)}, ${to(c.b)}, ${+c.a.toFixed(4)})`;
}

// main.css has no code background. These are the mockup's values (~/.cache/fleet-native-look/mockup/index.html).
const CODE_BG = { dark: "#101014", light: "#F7F6F3" };

export function buildScheme(vars: Vars, scheme: "dark" | "light") {
  const get = (name: string) => resolveVar(vars, name);
  const c = (name: string) => parseColor(get(name));
  const text = c("--text");
  const waiting = c("--status-waiting");
  const card = c("--card-bg");
  const border = c("--border");
  return {
    chrome: get("--main-bg"),
    panel: get("--panel-bg"),
    card: get("--card-bg"),
    border: get("--border"),
    text: get("--text"),
    muted: get("--muted"),
    accent: get("--accent"),
    accentDim: get("--accent-dim"),
    onAccent: get("--primary-foreground"),
    running: get("--running"),
    waiting: get("--status-waiting"),
    complete: get("--complete"),
    error: get("--error"),
    queued: get("--queued"),
    codeBg: CODE_BG[scheme],
    tint3: css(mix(text, 3, null)),
    tint5: css(mix(text, 5, null)),
    tint9: css(mix(text, 9, null)),
    rowText: css(mix(text, 86, null)),
    waitingBg: css(mix(waiting, 5, card)),
    waitingEdge: css(mix(waiting, 40, border)),
    diffAdd: get("--diff-add"),
    diffDel: get("--diff-del"),
  };
}

export function generate(mainCss: string): string {
  const dark = parseBlock(mainCss, ":root");
  const light = { ...dark, ...parseBlock(mainCss, ':root[data-theme="light"]') };
  const px = (name: string) => parseInt(resolveVar(dark, name), 10);
  const tokens = {
    dark: buildScheme(dark, "dark"),
    light: buildScheme(light, "light"),
    radius: { card: px("--radius-card"), button: px("--radius-btn"), panel: px("--radius-panel") },
  };
  return `// Generated from client/src/assets/main.css by mobile/scripts/tokens.ts. Do not edit.
// Regenerate with \`bun run tokens\` (in mobile/); \`bun run tokens:check\` fails when this file is out of date.
// codeBg is not in main.css: it is the mockup's value.
export const tokens = ${JSON.stringify(tokens, null, 2)} as const;
`;
}
