/**
 * The Enter that confirms an IME composition (Japanese, Chinese, Korean…) picks the candidate; it isn't "send".
 * Browsers mark it `isComposing`, and Safari reports it just after the composition ended with keyCode 229. Fleet has
 * many Enter-to-submit fields (composer, question card, rename, find bars, pickers), so one capture listener on the
 * window drops that key before any of them sees it. The composition itself is untouched: only propagation stops.
 */
export function isImeConfirmKey(event: KeyboardEvent): boolean {
  return event.key === "Enter" && (event.isComposing || event.keyCode === 229);
}

export function installImeEnterGuard(target: Window = window): () => void {
  const guard = (event: KeyboardEvent): void => {
    if (isImeConfirmKey(event)) event.stopImmediatePropagation();
  };
  target.addEventListener("keydown", guard, true);
  return () => target.removeEventListener("keydown", guard, true);
}
