import { expect, test } from "bun:test";
import { parseColor } from "../../scripts/tokens-lib";
import { tokens } from "./tokens.generated";
import { wordColor, type WordTone } from "./words";

type Rgb = [number, number, number];
const rgb = (value: string): Rgb => {
  const c = parseColor(value);
  return [c.r, c.g, c.b];
};
/** `c` at `a` over the opaque `bg`, as the phone draws alpha(hex, a). */
const over = (c: Rgb, a: number, bg: Rgb): Rgb => [0, 1, 2].map((i) => Math.round(c[i] * a + bg[i] * (1 - a))) as Rgb;
const luminance = (c: Rgb) => {
  const f = (x: number) => ((x /= 255) <= 0.03928 ? x / 12.92 : ((x + 0.055) / 1.055) ** 2.4);
  return 0.2126 * f(c[0]) + 0.7152 * f(c[1]) + 0.0722 * f(c[2]);
};
const contrast = (a: Rgb, b: Rgb) => {
  const [x, y] = [luminance(a), luminance(b)].sort((p, q) => q - p);
  return (x + 0.05) / (y + 0.05);
};

// Each word colour and the status colour whose tint it can sit on (a Pill 12%, a Chip 10%, a lit where-part 14%).
const WORDS = {
  accentText: "accent",
  runningText: "running",
  waitingText: "waiting",
  completeText: "complete",
  errorText: "error",
  queuedText: "queued",
  diffAddText: "diffAdd",
  diffDelText: "diffDel",
} as const;

for (const scheme of ["dark", "light"] as const) {
  test(`words in a status colour read at 4.5:1 on every surface and their own tint (${scheme})`, () => {
    const c = tokens[scheme];
    const failures: string[] = [];
    for (const [word, status] of Object.entries(WORDS)) {
      for (const surface of ["chrome", "panel", "card"] as const) {
        const bg = rgb(c[surface]);
        for (const [ground, shown] of [[surface, bg], [`14% ${status} over ${surface}`, over(rgb(c[status]), 0.14, bg)]] as const) {
          const ratio = contrast(rgb(c[word as keyof typeof c] as string), shown);
          if (ratio < 4.5) failures.push(`${word} on ${ground}: ${ratio.toFixed(2)}`);
        }
      }
    }
    expect(failures).toEqual([]);
  });
}

test("a tone's words take its -Text colour, never the status colour", () => {
  const c = tokens.light;
  const tones: Record<WordTone, string> = {
    default: c.text, muted: c.muted, accent: c.accentText, error: c.errorText, waiting: c.waitingText, running: c.runningText,
  };
  for (const [tone, expected] of Object.entries(tones)) expect(wordColor(tone as WordTone, c)).toBe(expected);
  // Light's status colours are 3:1 marks; as words they were 2.9:1 (running) and 3.4:1 (waiting).
  expect(wordColor("running", c)).not.toBe(c.running);
  expect(wordColor("waiting", c)).not.toBe(c.waiting);
});
