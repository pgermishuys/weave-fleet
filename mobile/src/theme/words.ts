import type { tokens } from "./tokens.generated";

export type Colors = { [K in keyof typeof tokens.dark]: string };
export type WordTone = "default" | "muted" | "accent" | "error" | "waiting" | "running";

/**
 * The colour words take in a tone. A status colour itself (`running`, `error`, …) is for dots, icons and tints, 3:1;
 * words in it use its `-Text` colour, 4.5:1 on every surface and its own tint (desktop's --running-text and friends).
 */
export function wordColor(tone: WordTone, c: Colors): string {
  switch (tone) {
    case "default": return c.text;
    case "muted": return c.muted;
    case "accent": return c.accentText;
    case "error": return c.errorText;
    case "waiting": return c.waitingText;
    case "running": return c.runningText;
  }
}
