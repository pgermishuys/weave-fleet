export interface TurnErrorText {
  /** What went wrong, in the harness's words, without its stack trace. */
  summary: string;
  /** The stack trace the harness put after the message, if any. */
  details: string | null;
}

// A stack frame: "at fn (/path/file.js:12:34)" or "at /path/file.js:12:34".
const STACK_FRAME = /\s+at\s+[^\n]*?:\d+:\d+\)?/;
const ERROR_PREFIX = /^[A-Z][\w$]*Error:\s+/;

/**
 * Splits a turn failure's message into what to show and the stack trace to keep folded. Harnesses such as OpenCode put
 * the whole trace in the message, which buried the one sentence the reader needs.
 */
export function splitTurnErrorMessage(message: string): TurnErrorText {
  const text = message.trim();
  const frame = STACK_FRAME.exec(text);
  const head = frame ? text.slice(0, frame.index).trim() : text;
  const details = frame ? text.slice(frame.index).trim() : null;
  const withoutPrefix = head.replace(ERROR_PREFIX, "");
  return { summary: withoutPrefix || head || text, details };
}
