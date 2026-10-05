import { shallowRef } from "vue";

/** A short message at the bottom of the phone screen ("Archived · Undo"), gone after a few seconds. */
export interface PhoneToast {
  id: number;
  text: string;
  action?: { label: string; run: () => void };
}

export const currentToast = shallowRef<PhoneToast | null>(null);
let nextId = 1;
let timer: ReturnType<typeof setTimeout> | undefined;

export function showToast(text: string, action?: PhoneToast["action"], ms = 3800): void {
  clearTimeout(timer);
  const toast = { id: nextId++, text, ...(action ? { action } : {}) };
  currentToast.value = toast;
  timer = setTimeout(() => {
    if (currentToast.value?.id === toast.id) currentToast.value = null;
  }, ms);
}

export function hideToast(): void {
  clearTimeout(timer);
  currentToast.value = null;
}
