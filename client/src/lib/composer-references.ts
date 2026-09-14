/**
 * `@` references in a draft: `@src/app.ts`, `@src/components/`, `@shuttle`. They're plain text in
 * the message (the agent reads the path); the composer only draws them differently.
 */
export interface DraftSegment {
  text: string;
  reference?: "file" | "folder";
}

// "@" at the start or after whitespace, then everything up to the next whitespace.
const REFERENCE_PATTERN = /(^|\s)(@[^\s@][^\s]*)/g;
// Sentence punctuation typed straight after a reference isn't part of it.
const TRAILING_PUNCTUATION = /[.,;:!?)\]}'"]+$/;

/**
 * Splits a draft into plain text and references. The token the caret is in (or at the end of) is
 * still being typed, so it stays plain until you move past it; picking from the `@` popup adds a
 * space after the reference, which does that.
 */
export function splitDraftReferences(text: string, caret: number | null = null): DraftSegment[] {
  const segments: DraftSegment[] = [];
  let plainStart = 0;

  for (const match of text.matchAll(REFERENCE_PATTERN)) {
    const token = match[2].replace(TRAILING_PUNCTUATION, "");
    const start = (match.index ?? 0) + match[1].length;
    const end = start + token.length;
    const tokenEnd = start + match[2].length;

    if (token.length < 2 || (caret !== null && caret > start && caret <= tokenEnd)) {
      continue;
    }

    if (start > plainStart) {
      segments.push({ text: text.slice(plainStart, start) });
    }
    segments.push({ text: token, reference: token.endsWith("/") ? "folder" : "file" });
    plainStart = end;
  }

  if (plainStart < text.length) {
    segments.push({ text: text.slice(plainStart) });
  }

  return segments;
}
