/**
 * True when the user is typing somewhere outside `card`: a text field, textarea or editable region that has focus and
 * has something in it. An empty composer that took focus as the session opened is not typing.
 */
function isTypingElsewhere(card: HTMLElement): boolean {
  const active = document.activeElement;
  if (!(active instanceof HTMLElement) || card.contains(active)) return false;
  if (active instanceof HTMLTextAreaElement || (active instanceof HTMLInputElement && active.type === "text")) {
    return active.value.length > 0;
  }
  return active.isContentEditable && (active.textContent ?? "").length > 0;
}

/**
 * A card that blocks the turn (a question, a permission ask) takes keyboard focus when it appears: its first
 * choice, or the card itself. Focus stays put when the user is typing in the composer, so no sentence is interrupted.
 */
export function focusBlockingCard(card: HTMLElement | null, firstChoice: string): void {
  if (!card || isTypingElsewhere(card)) return;
  const target = card.querySelector<HTMLElement>(`${firstChoice}:not(:disabled)`) ?? card;
  target.focus({ preventScroll: false });
}

/** Arrow keys move between a card's choices (Up/Left back, Down/Right forward), wrapping round. */
export function moveChoiceFocus(event: KeyboardEvent, card: HTMLElement | null, choices: string): boolean {
  if (!card || event.metaKey || event.ctrlKey || event.altKey) return false;
  const delta = event.key === "ArrowDown" || event.key === "ArrowRight" ? 1
    : event.key === "ArrowUp" || event.key === "ArrowLeft" ? -1 : 0;
  if (delta === 0 || !(event.target instanceof HTMLButtonElement)) return false;
  const items = [...card.querySelectorAll<HTMLElement>(choices)].filter((el) => !el.matches(":disabled"));
  const at = items.indexOf(event.target);
  if (at < 0) return false;
  event.preventDefault();
  items[(at + delta + items.length) % items.length]?.focus();
  return true;
}
