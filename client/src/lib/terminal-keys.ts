import type { GlobalShortcut } from "@/lib/command-registry";
import { matchesKeyboardShortcut } from "@/composables/use-keyboard-shortcut";

/**
 * Who gets a key pressed while the terminal has focus. The shell gets
 * everything (Esc, Ctrl K, Ctrl B, Ctrl [ … are everyday shell keys) except
 * Fleet's own pass-through shortcuts and, off a Mac, Ctrl Shift C and
 * Ctrl Shift V for copy and paste (Ctrl C stops the running command).
 */
export type TerminalKeyOwner =
  /** Fleet handles it: the terminal ignores it and lets it through. */
  | "fleet"
  /** Copy the terminal's selection. */
  | "copy"
  /** Let the browser paste; the terminal sends what's pasted to the shell. */
  | "paste"
  /** The shell gets it, and Fleet never sees it. */
  | "shell";

export function terminalKeyOwner(event: KeyboardEvent, isMac: boolean, passThrough: readonly GlobalShortcut[]): TerminalKeyOwner {
  if (passThrough.some((shortcut) => matchesKeyboardShortcut(event, shortcut))) return "fleet";

  if (!isMac && event.ctrlKey && event.shiftKey && !event.altKey && !event.metaKey) {
    const key = event.key.toLowerCase();
    if (key === "c") return "copy";
    if (key === "v") return "paste";
  }

  return "shell";
}
