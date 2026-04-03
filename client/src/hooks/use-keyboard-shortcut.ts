"use client";

import { useEffect } from "react";

interface KeyboardShortcutOptions {
  /** Require metaKey (⌘ on macOS) */
  metaKey?: boolean;
  /** Require ctrlKey */
  ctrlKey?: boolean;
  /**
   * When true, uses metaKey on macOS and ctrlKey on other platforms.
   * Takes precedence over individual metaKey/ctrlKey options.
   */
  platformModifier?: boolean;
}

/**
 * Registers a global keydown listener for a keyboard shortcut.
 * Skips when focus is inside text inputs / contenteditable elements.
 *
 * NOTE: `callback` should be wrapped in `useCallback` to avoid
 * re-registering the listener on every render.
 */
export function useKeyboardShortcut(
  key: string,
  callback: () => void,
  options: KeyboardShortcutOptions = {}
): void {
  useEffect(() => {
    const isMac =
      typeof navigator !== "undefined" &&
      /Mac|iPhone|iPad|iPod/.test(navigator.userAgent);

    const handleKeyDown = (e: KeyboardEvent) => {
      // Skip when focus is inside a text field
      const target = e.target as HTMLElement | null;
      if (
        target instanceof HTMLInputElement ||
        target instanceof HTMLTextAreaElement ||
        target?.isContentEditable
      ) {
        return;
      }

      if (e.key !== key) return;

      // Determine if the required modifier is pressed
      let modifierOk = false;
      if (options.platformModifier) {
        // Platform-aware: ⌘ on macOS, Ctrl elsewhere
        modifierOk = isMac ? e.metaKey : e.ctrlKey;
      } else {
        // Explicit modifier requirements
        if (options.metaKey && e.metaKey) modifierOk = true;
        if (options.ctrlKey && e.ctrlKey) modifierOk = true;
        // If no modifier options specified, no modifier required
        if (!options.metaKey && !options.ctrlKey) modifierOk = true;
      }

      if (!modifierOk) return;

      e.preventDefault();
      callback();
    };

    document.addEventListener("keydown", handleKeyDown);
    return () => document.removeEventListener("keydown", handleKeyDown);
  }, [key, callback, options.metaKey, options.ctrlKey, options.platformModifier]);
}
