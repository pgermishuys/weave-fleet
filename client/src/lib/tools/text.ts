/** Shorten a file path to `…/parentDir/filename` when it exceeds `maxLen` chars. */
export function shortenPath(filePath: string, maxLen = 50): string {
  if (filePath.length <= maxLen) return filePath;
  const segments = filePath.split("/");
  if (segments.length <= 2) return filePath;
  return "…/" + segments.slice(-2).join("/");
}

/** Truncate a string to `maxLen` chars, appending "…" if trimmed. */
export function truncate(value: string, maxLen: number): string {
  if (value.length <= maxLen) return value;
  return value.slice(0, maxLen) + "…";
}

/** `read` → `Read`. */
export function capitalize(value: string): string {
  return value.charAt(0).toUpperCase() + value.slice(1);
}
