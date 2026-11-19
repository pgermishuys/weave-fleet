/**
 * The key of a shortcut: what to compare a shortcut's letter or digit with.
 *
 * On a Latin layout `event.key` is the letter ("k"), on AZERTY or Dvorak too, so it is used as is. On a layout without
 * Latin letters (Cyrillic, Greek, Hebrew, Arabic) `event.key` is "л" and Ctrl+K would never match, so while Ctrl or ⌘ is
 * held the physical key (`event.code`, "KeyK") stands in. Plain typing is never remapped.
 */
export function shortcutKey(event: Pick<KeyboardEvent, "key" | "code" | "ctrlKey" | "metaKey">): string {
  const key = event.key.length === 1 ? event.key.toLowerCase() : event.key;
  if (!(event.ctrlKey || event.metaKey)) return key;
  if (/^[a-z0-9]$/.test(key)) return key;
  if (key.length !== 1) return key;
  const letter = /^Key([A-Z])$/.exec(event.code ?? "");
  if (letter) return letter[1].toLowerCase();
  const digit = /^Digit([0-9])$/.exec(event.code ?? "");
  if (digit) return digit[1];
  return key;
}
