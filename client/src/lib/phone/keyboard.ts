/**
 * iOS only raises the keyboard for a field focused inside the user's tap. A sheet that opens after navigating (New
 * session) mounts too late for that, so the tap focuses an invisible field first to bring the keyboard up, and the
 * sheet hands focus to its own field when it's in (`takeKeyboard`).
 */
let proxy: HTMLInputElement | null = null;

export function holdKeyboard(): void {
  if (typeof document === "undefined") return;
  proxy?.remove();
  proxy = document.createElement("input");
  proxy.setAttribute("aria-hidden", "true");
  proxy.tabIndex = -1;
  // 16px, so iOS doesn't zoom; on screen but invisible, so iOS doesn't scroll to it.
  proxy.style.cssText = "position:fixed;top:0;left:0;width:1px;height:1px;opacity:0;font-size:16px;border:0;padding:0;pointer-events:none";
  document.body.appendChild(proxy);
  proxy.focus({ preventScroll: true });
}

/** Moves the held keyboard to `field` (or just focuses it) and drops the stand-in. */
export function takeKeyboard(field: HTMLElement | null | undefined): void {
  field?.focus({ preventScroll: true });
  proxy?.remove();
  proxy = null;
}

/** Whether a tap is holding the keyboard for a sheet that's on its way. */
export function keyboardHeld(): boolean {
  return proxy !== null;
}

/** The most a growing field shows before it scrolls: five lines of 24px, and its padding. */
export function grownHeight(scrollHeight: number, lines = 5, lineHeight = 24, padding = 14): number {
  return Math.min(scrollHeight, lines * lineHeight + padding);
}

/** Grows a text box with what's typed, up to five lines, then it scrolls. */
export function autogrow(field: HTMLTextAreaElement | null | undefined, minHeight = 0): void {
  if (!field) return;
  field.style.height = "auto";
  field.style.height = `${Math.max(minHeight, grownHeight(field.scrollHeight))}px`;
}
