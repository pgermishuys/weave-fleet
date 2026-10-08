/** Longest gist kept; the line is cut to its width anyway. */
const MAX_GIST = 240;

/** The lines of `text` with Markdown line markers (`#`, `>`, `-`, `1.`) taken off, empty ones dropped. */
function lines(text: string): string[] {
  return text
    .split(/\r?\n/)
    .map((line) => line.trim().replace(/^(?:#{1,6}\s+|>\s*|[-*+]\s+|\d+[.)]\s+)/, "").trim())
    .filter(Boolean);
}

function sentences(line: string): string[] {
  return line.split(/(?<=[.!?])\s+/).filter(Boolean);
}

/**
 * The one line a folded thinking block shows. A finished block shows its summary when the harness sent one, otherwise
 * its first sentence. A block the model is still writing shows its newest sentence, so the line follows along.
 */
export function reasoningGist(text: string, summary?: string, live = false): string {
  let gist = "";
  if (live) {
    gist = sentences(lines(text).at(-1) ?? "").at(-1) ?? "";
  } else {
    gist = lines(summary ?? "")[0] ?? sentences(lines(text)[0] ?? "")[0] ?? "";
  }
  return gist.length > MAX_GIST ? `${gist.slice(0, MAX_GIST)}…` : gist;
}
