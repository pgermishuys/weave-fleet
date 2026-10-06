import { shallowRef } from "vue";
import { ARCHIVE_UNDO_MS } from "@/stores/archive-queue";

/**
 * A short message at the bottom of the phone screen, inverted as desktop's Undo toast is ("Archived “…” · Undo"),
 * gone after a few seconds. One with an action (Undo) lasts as long as the desktop's and drains a bar while it can
 * still be undone.
 */
export interface PhoneToast {
  id: number;
  text: string;
  ms: number;
  action?: { label: string; run: () => void };
}

export const currentToast = shallowRef<PhoneToast | null>(null);
let nextId = 1;
let timer: ReturnType<typeof setTimeout> | undefined;

export function showToast(text: string, action?: PhoneToast["action"], ms = action ? ARCHIVE_UNDO_MS : 3800): void {
  clearTimeout(timer);
  const toast = { id: nextId++, text, ms, ...(action ? { action } : {}) };
  currentToast.value = toast;
  timer = setTimeout(() => {
    if (currentToast.value?.id === toast.id) currentToast.value = null;
  }, ms);
}

export function hideToast(): void {
  clearTimeout(timer);
  currentToast.value = null;
}
