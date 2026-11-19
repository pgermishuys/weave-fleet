import { shallowRef } from "vue";

/**
 * Which machine's own page Settings → Machines shows, by key; null for the list. Shared with the Settings header, which
 * turns into the machine's name with a way back to "Machines" while a page is open.
 */
const openKey = shallowRef<string | null>(null);

export function useMachinePage() {
  return {
    openKey,
    open(key: string): void {
      openKey.value = key;
    },
    close(): void {
      openKey.value = null;
    },
  };
}
