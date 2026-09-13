/**
 * Terminal lines the user added to their message. The agent never reads the
 * terminal on its own; this is the only way its output reaches the agent.
 */
export interface TerminalContextDraft {
  id: string;
  terminalId: string;
  /** The terminal's tab title, e.g. "zsh" or "zsh 2". */
  label: string;
  /** First and last line, counted from the top of the terminal's scrollback, starting at 1. */
  from: number;
  to: number;
  text: string;
}

/** "line 9" or "lines 9–11". */
export function terminalLineRange(from: number, to: number): string {
  return from === to ? `line ${from}` : `lines ${from}–${to}`;
}

/**
 * The message as sent: each attachment as a fenced block with its label, then
 * what the user typed. The fence is longer than any run of backticks inside,
 * so output that itself contains a fence stays inside the block.
 */
export function formatTerminalContext(contexts: readonly TerminalContextDraft[], message: string): string {
  if (contexts.length === 0) return message;

  const blocks = contexts.map((context) => {
    const longest = Math.max(0, ...[...context.text.matchAll(/`+/g)].map((match) => match[0].length));
    const fence = "`".repeat(Math.max(3, longest + 1));
    return `Terminal ${context.label}, ${terminalLineRange(context.from, context.to)}:\n${fence}text\n${context.text}\n${fence}`;
  });

  const typed = message.trim();
  return typed ? `${blocks.join("\n\n")}\n\n${typed}` : blocks.join("\n\n");
}
