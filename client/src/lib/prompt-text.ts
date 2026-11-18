/**
 * What a prompt is sent as: blank lines before the first line and spaces after the last are dropped, the indent of
 * the first line is not. A prompt that starts with an indented code block must reach the agent (and the bubble)
 * with that indent, so this is not `trim()`.
 */
export function tidyPrompt(text: string): string {
  return text.replace(/^(?:[ \t]*\r?\n)+/, "").trimEnd();
}
