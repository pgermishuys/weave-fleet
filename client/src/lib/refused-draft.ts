/**
 * The draft after a refused send: what was sent comes back in front, a blank line, then whatever was typed while the
 * send was pending. Nothing typed since (or the same text again) leaves just the refused text.
 */
export function draftAfterRefusal(refused: string, current: string): string {
  if (current.trim().length === 0 || current === refused) return refused;
  return `${refused.trimEnd()}\n\n${current}`;
}
