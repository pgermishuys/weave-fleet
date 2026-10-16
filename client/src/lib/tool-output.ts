/**
 * A tool's output as Fleet draws it: terminal colour codes (ANSI SGR) become styled spans in Fleet's palette, every
 * other escape sequence is dropped, and a line rewritten with `\r` (a progress bar) keeps only its last frame. A span
 * is plain text plus a class (or, for 256-colour and true-colour codes, a colour); `linesToHtml` escapes the text.
 */

export interface OutputSpan {
  text: string;
  /** Space-separated `ansi-*` classes: palette colours, bold, dim, italic, underline. */
  className?: string;
  /** A colour outside the 16-colour palette, as an inline style (`color:rgb(…)`). Built from numbers only. */
  style?: string;
}

export type OutputLine = OutputSpan[];

export interface ParsedOutput {
  lines: OutputLine[];
  /** The output with no codes and only the last frame of each `\r` line, as Copy gives it. */
  plain: string;
}

const PALETTE = ["black", "red", "green", "yellow", "blue", "magenta", "cyan", "white"] as const;

interface Style {
  fg?: string;
  bg?: string;
  fgRgb?: string;
  bgRgb?: string;
  bold: boolean;
  dim: boolean;
  italic: boolean;
  underline: boolean;
}

function plainStyle(): Style {
  return { bold: false, dim: false, italic: false, underline: false };
}

/** A control character Fleet acts on or drops: everything below space except tab, plus DEL. */
const CONTROL = /[\x00-\x08\x0a-\x1f\x7f]/g;
/** CSI: `ESC [`, parameter bytes, intermediate bytes, a final byte. */
const CSI = /\x1b\[([0-?]*)[ -/]*([@-~])/y;
/** OSC (window titles, hyperlinks): ended by BEL or `ESC \`; one with no end stops at the next escape or line. */
const OSC = /\x1b\][^\x07\x1b\n]*(?:\x07|\x1b\\|(?=[\x1b\n])|$)/y;
/** DCS, SOS, PM, APC strings, ended by `ESC \` (or, broken, at the next escape or line). */
const STRING = /\x1b[PX^_][^\x1b\n]*(?:\x1b\\|(?=[\x1b\n])|$)/y;
/** Character-set picks (`ESC ( B`) and the other two- and three-byte sequences. */
const SHORT = /\x1b[ -/]*[0-~]/y;

function rgb(r: number, g: number, b: number): string {
  return `rgb(${r},${g},${b})`;
}

/** A 256-colour index: 0–15 are the palette, the rest the 6×6×6 cube and the grey ramp. */
function color256(index: number): { name?: string; rgb?: string } {
  if (index < 8) return { name: PALETTE[index] };
  if (index < 16) return { name: `bright-${PALETTE[index - 8]}` };
  if (index < 232) {
    const n = index - 16;
    const level = (v: number) => (v === 0 ? 0 : 55 + v * 40);
    return { rgb: rgb(level(Math.floor(n / 36)), level(Math.floor(n / 6) % 6), level(n % 6)) };
  }
  const grey = 8 + (index - 232) * 10;
  return { rgb: rgb(grey, grey, grey) };
}

function byte(value: number | undefined): number {
  return Math.max(0, Math.min(255, Number.isFinite(value) ? (value as number) : 0));
}

/** Applies one SGR sequence's parameters (`1;31`, `38;5;208`, …) to the running style. */
function applySgr(style: Style, params: string): Style {
  const codes = params === "" ? [0] : params.split(/[;:]/).map((p) => (p === "" ? 0 : Number(p)));
  let next = { ...style };
  for (let i = 0; i < codes.length; i++) {
    const code = codes[i] as number;
    if (code === 0) next = plainStyle();
    else if (code === 1) next.bold = true;
    else if (code === 2) next.dim = true;
    else if (code === 3) next.italic = true;
    else if (code === 4) next.underline = true;
    else if (code === 22) next.bold = next.dim = false;
    else if (code === 23) next.italic = false;
    else if (code === 24) next.underline = false;
    else if (code >= 30 && code <= 37) [next.fg, next.fgRgb] = [PALETTE[code - 30], undefined];
    else if (code === 39) next.fg = next.fgRgb = undefined;
    else if (code >= 40 && code <= 47) [next.bg, next.bgRgb] = [PALETTE[code - 40], undefined];
    else if (code === 49) next.bg = next.bgRgb = undefined;
    else if (code >= 90 && code <= 97) [next.fg, next.fgRgb] = [`bright-${PALETTE[code - 90]}`, undefined];
    else if (code >= 100 && code <= 107) [next.bg, next.bgRgb] = [`bright-${PALETTE[code - 100]}`, undefined];
    else if (code === 38 || code === 48) {
      const mode = codes[i + 1];
      let picked: { name?: string; rgb?: string } | undefined;
      if (mode === 5) {
        picked = color256(byte(codes[i + 2]));
        i += 2;
      } else if (mode === 2) {
        picked = { rgb: rgb(byte(codes[i + 2]), byte(codes[i + 3]), byte(codes[i + 4])) };
        i += 4;
      } else {
        // An extended colour with no mode says nothing more; the rest of the sequence is still read.
        i += 1;
        continue;
      }
      if (code === 38) [next.fg, next.fgRgb] = [picked.name, picked.rgb];
      else [next.bg, next.bgRgb] = [picked.name, picked.rgb];
    }
    // Inverse, blink, strikethrough, fonts and the rest draw nothing in Fleet.
  }
  return next;
}

function spanFor(style: Style, text: string): OutputSpan {
  const classes: string[] = [];
  if (style.fg) classes.push(`ansi-fg-${style.fg}`);
  if (style.bg) classes.push(`ansi-bg-${style.bg}`);
  if (style.bold) classes.push("ansi-bold");
  if (style.dim) classes.push("ansi-dim");
  if (style.italic) classes.push("ansi-italic");
  if (style.underline) classes.push("ansi-underline");
  const styles: string[] = [];
  if (style.fgRgb) styles.push(`color:${style.fgRgb}`);
  if (style.bgRgb) styles.push(`background-color:${style.bgRgb}`);
  const span: OutputSpan = { text };
  if (classes.length > 0) span.className = classes.join(" ");
  if (styles.length > 0) span.style = styles.join(";");
  return span;
}

function sameLook(a: OutputSpan, b: OutputSpan): boolean {
  return a.className === b.className && a.style === b.style;
}

/** Parses a tool's output for drawing. Trailing blank lines are dropped; an empty output has no lines. */
export function parseToolOutput(raw: string): ParsedOutput {
  const lines: OutputLine[] = [];
  let line: OutputLine = [];
  let style = plainStyle();
  // A `\r` starts the next frame of the line; the old frame goes when the new one prints something, so a line that
  // ends in `\r` keeps what it showed.
  let rewind = false;

  const push = (text: string) => {
    if (text === "") return;
    if (rewind) {
      line = [];
      rewind = false;
    }
    const span = spanFor(style, text);
    const last = line[line.length - 1];
    if (last && sameLook(last, span)) last.text += text;
    else line.push(span);
  };

  let at = 0;
  CONTROL.lastIndex = 0;
  while (at < raw.length) {
    CONTROL.lastIndex = at;
    const found = CONTROL.exec(raw);
    if (!found) {
      push(raw.slice(at));
      break;
    }
    push(raw.slice(at, found.index));
    at = found.index;
    const char = raw[at];
    if (char === "\n") {
      lines.push(line);
      line = [];
      rewind = false;
      at++;
    } else if (char === "\r") {
      // `\r\n` is a plain line end.
      if (raw[at + 1] !== "\n") rewind = true;
      at++;
    } else if (char === "\x1b") {
      at = skipEscape(raw, at, (params) => {
        style = applySgr(style, params);
      });
    } else {
      // Bell, backspace and the other controls draw nothing.
      at++;
    }
  }
  lines.push(line);

  while (lines.length > 0 && isBlank(lines[lines.length - 1] as OutputLine)) lines.pop();
  return { lines, plain: lines.map(lineText).join("\n") };
}

/** Reads the escape sequence at `at`, calls `onSgr` for a colour sequence, and returns where the text resumes. */
function skipEscape(raw: string, at: number, onSgr: (params: string) => void): number {
  CSI.lastIndex = at;
  const csi = CSI.exec(raw);
  if (csi) {
    // Only plain SGR (`ESC [ 1;31 m`) colours text; private ones (`ESC [ ? 25 h`) and cursor moves are dropped.
    if (csi[2] === "m" && /^[\d;:]*$/.test(csi[1] as string)) onSgr(csi[1] as string);
    return CSI.lastIndex;
  }
  for (const pattern of [OSC, STRING, SHORT]) {
    pattern.lastIndex = at;
    if (pattern.exec(raw)) return pattern.lastIndex;
  }
  // A broken sequence (an `ESC` at the end, or `ESC [` with no final byte): drop the `ESC` and keep the rest as text.
  return at + 1;
}

function isBlank(line: OutputLine): boolean {
  return line.every((span) => span.text.trim() === "");
}

export function lineText(line: OutputLine): string {
  let text = "";
  for (const span of line) text += span.text;
  return text;
}

const HTML_ESCAPES: Record<string, string> = { "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" };

function escapeHtml(text: string): string {
  return text.replace(/[&<>"']/g, (char) => HTML_ESCAPES[char] as string);
}

/**
 * Lines as markup for the output box: every bit of text escaped, uncoloured text bare, coloured text in a span whose
 * class and style Fleet built (palette names and numbers, never the output). One string per box keeps a 2,000-line
 * box to a few thousand nodes and one parse by the browser.
 */
export function linesToHtml(lines: readonly OutputLine[]): string {
  let html = "";
  for (let i = 0; i < lines.length; i++) {
    if (i > 0) html += "\n";
    for (const span of lines[i] as OutputLine) {
      const text = escapeHtml(span.text);
      if (!span.className && !span.style) {
        html += text;
        continue;
      }
      html += "<span";
      if (span.className) html += ` class="${span.className}"`;
      if (span.style) html += ` style="${span.style}"`;
      html += `>${text}</span>`;
    }
  }
  return html;
}

const CACHE_SIZE = 32;
const cache = new Map<string, ParsedOutput>();

/**
 * `parseToolOutput`, remembered for the last few outputs: a row that's closed and opened again, or redrawn when the
 * conversation re-renders, doesn't parse its output twice.
 */
export function parseToolOutputCached(raw: string): ParsedOutput {
  const hit = cache.get(raw);
  if (hit) {
    cache.delete(raw);
    cache.set(raw, hit);
    return hit;
  }
  const parsed = parseToolOutput(raw);
  cache.set(raw, parsed);
  if (cache.size > CACHE_SIZE) cache.delete(cache.keys().next().value as string);
  return parsed;
}
