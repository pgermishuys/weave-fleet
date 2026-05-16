import { ref, type Ref } from "vue";

const STORAGE_KEY_PREFIX = "weave:input-history:";
const MAX_HISTORY = 10;

function storageKey(sessionId: string): string {
  return `${STORAGE_KEY_PREFIX}${sessionId}`;
}

function loadHistory(sessionId: string): string[] {
  try {
    const raw = localStorage.getItem(storageKey(sessionId));
    if (!raw) return [];
    const parsed = JSON.parse(raw);
    return Array.isArray(parsed) ? parsed.slice(0, MAX_HISTORY) : [];
  } catch {
    return [];
  }
}

function saveHistory(sessionId: string, entries: string[]): void {
  try {
    localStorage.setItem(storageKey(sessionId), JSON.stringify(entries.slice(0, MAX_HISTORY)));
  } catch {
    // localStorage may be unavailable
  }
}

export interface InputHistory {
  /** All stored history entries (most recent first). */
  entries: Ref<string[]>;
  /** Whether the history popup is currently visible. */
  isOpen: Ref<boolean>;
  /** Index of the currently highlighted item (-1 = none). */
  selectedIndex: Ref<number>;
  /** Record a sent message into history. */
  push: (text: string) => void;
  /** Open the history popup. */
  open: () => void;
  /** Close the history popup and reset selection. */
  close: () => void;
  /** Move selection up (toward older). */
  moveUp: () => void;
  /** Move selection down (toward newer). */
  moveDown: () => void;
  /** Return the currently selected entry, or undefined. */
  confirm: () => string | undefined;
}

export function useInputHistory(sessionId: string): InputHistory {
  const entries = ref<string[]>(loadHistory(sessionId));
  const isOpen = ref(false);
  const selectedIndex = ref(-1);

  function push(text: string): void {
    const trimmed = text.trim();
    if (!trimmed) return;

    // Remove duplicate if already present
    const filtered = entries.value.filter((e) => e !== trimmed);
    filtered.unshift(trimmed);
    entries.value = filtered.slice(0, MAX_HISTORY);
    saveHistory(sessionId, entries.value);
  }

  function open(): void {
    if (entries.value.length === 0) return;
    isOpen.value = true;
    selectedIndex.value = 0;
  }

  function close(): void {
    isOpen.value = false;
    selectedIndex.value = -1;
  }

  function moveUp(): void {
    if (!isOpen.value) return;
    if (selectedIndex.value > 0) {
      selectedIndex.value--;
    }
  }

  function moveDown(): void {
    if (!isOpen.value) return;
    if (selectedIndex.value < entries.value.length - 1) {
      selectedIndex.value++;
    }
  }

  function confirm(): string | undefined {
    if (!isOpen.value || selectedIndex.value < 0) return undefined;
    const entry = entries.value[selectedIndex.value];
    close();
    return entry;
  }

  return { entries, isOpen, selectedIndex, push, open, close, moveUp, moveDown, confirm };
}
