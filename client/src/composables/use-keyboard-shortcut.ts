import { onMounted, onUnmounted, toValue, type MaybeRefOrGetter } from "vue";
import type { GlobalShortcut } from "@/lib/command-registry";

export interface KeyboardShortcutOptions {
  metaKey?: boolean;
  ctrlKey?: boolean;
  platformModifier?: boolean;
  enabled?: MaybeRefOrGetter<boolean>;
  allowInEditable?: boolean;
}

type KeyboardShortcutInput = string | GlobalShortcut | null | undefined;

function isApplePlatform(): boolean {
  return typeof navigator !== "undefined" && /Mac|iPhone|iPad|iPod/.test(navigator.userAgent);
}

export function isEditableKeyboardTarget(target: EventTarget | null): boolean {
  return target instanceof HTMLInputElement
    || target instanceof HTMLTextAreaElement
    || target instanceof HTMLSelectElement
    || (target instanceof HTMLElement && target.isContentEditable);
}

function normalizeKey(key: string): string {
  return key.length === 1 ? key.toLowerCase() : key;
}

export function matchesKeyboardShortcut(event: KeyboardEvent, shortcut: GlobalShortcut): boolean {
  if (normalizeKey(event.key) !== normalizeKey(shortcut.key)) {
    return false;
  }

  if (shortcut.platformModifier) {
    return isApplePlatform() ? event.metaKey : event.ctrlKey;
  }

  if (shortcut.metaKey || shortcut.ctrlKey) {
    return Boolean((shortcut.metaKey && event.metaKey) || (shortcut.ctrlKey && event.ctrlKey));
  }

  return !event.metaKey && !event.ctrlKey && !event.altKey;
}

function resolveShortcut(input: KeyboardShortcutInput, options: KeyboardShortcutOptions): GlobalShortcut | null {
  if (!input) {
    return null;
  }

  if (typeof input === "string") {
    return {
      key: input,
      metaKey: options.metaKey,
      ctrlKey: options.ctrlKey,
      platformModifier: options.platformModifier,
    };
  }

  return input;
}

export function useKeyboardShortcut(
  shortcut: MaybeRefOrGetter<KeyboardShortcutInput>,
  callback: () => void,
  options: KeyboardShortcutOptions = {},
): void {
  function handleKeyDown(event: KeyboardEvent): void {
    if (toValue(options.enabled) === false) {
      return;
    }

    if (!options.allowInEditable && isEditableKeyboardTarget(event.target)) {
      return;
    }

    const resolvedShortcut = resolveShortcut(toValue(shortcut), options);
    if (!resolvedShortcut || !matchesKeyboardShortcut(event, resolvedShortcut)) {
      return;
    }

    event.preventDefault();
    callback();
  }

  onMounted(() => {
    document.addEventListener("keydown", handleKeyDown);
  });

  onUnmounted(() => {
    document.removeEventListener("keydown", handleKeyDown);
  });
}
