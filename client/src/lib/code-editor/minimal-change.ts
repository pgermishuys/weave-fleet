import type { Text } from "@codemirror/state";

export interface MinimalChange {
  from: number;
  to: number;
  /** The replacement, as lines, so it keeps line breaks whatever the state's separator is. */
  insert: Text;
  /** 1-based lines of the new document that the change covers. */
  lines: { from: number; to: number };
}

/**
 * The smallest single replacement that turns `before` into `after`: the common start and end stay,
 * so the cursor, selection and scroll mostly stay put when the agent's edit is applied.
 * Positions are document positions, where a line break always counts as one.
 */
export function minimalChange(before: Text, after: Text): MinimalChange | null {
  if (before.eq(after)) return null;

  const a = before.toString();
  const b = after.toString();
  let start = 0;
  const shorter = Math.min(a.length, b.length);
  while (start < shorter && a.charCodeAt(start) === b.charCodeAt(start)) start++;

  let end = 0;
  while (
    end < shorter - start
    && a.charCodeAt(a.length - 1 - end) === b.charCodeAt(b.length - 1 - end)
  ) {
    end++;
  }

  const insertTo = b.length - end;
  return {
    from: start,
    to: a.length - end,
    insert: after.slice(start, insertTo),
    lines: { from: after.lineAt(start).number, to: after.lineAt(Math.max(start, insertTo)).number },
  };
}
