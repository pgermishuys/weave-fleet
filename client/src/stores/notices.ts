import { defineStore } from "pinia";
import { computed, shallowRef, type Component } from "vue";

export interface NoticeAction {
  label: string;
  icon?: Component;
  /** primary: filled accent. danger: red wash, for an action that stops work. quiet: text only (the default). */
  tone?: "primary" | "danger" | "quiet";
  run: () => void | Promise<void>;
}

/** What a notice's card shows. */
export interface NoticeContent {
  title: string;
  body?: string;
  icon?: Component;
  /** warn tints the icon amber, for a notice that asks before something stops work. */
  tone?: "accent" | "warn";
  actions?: NoticeAction[];
  /** A link after the body, opened in the browser. */
  link?: { label: string; href: string };
}

export interface Notice extends NoticeContent {
  /** Stable key. A notice whose id has been shown before comes back settled, as its chip, never as a card. */
  id: string;
  /** What the status bar chip says once the card settles. Without one, the notice goes away when it settles. */
  chip?: string;
  /** Only ever a chip: it never opens as a card by itself. */
  quiet?: boolean;
  /** Goes away this long after it was posted. */
  expiresMs?: number;
  /** Clicking the chip runs this instead of opening the card. */
  onChipClick?: () => void;
}

/** How long a card stays before it settles into its chip. Hovering or focusing it holds it. */
export const NOTICE_HOLD_MS = 8000;
/** A card waits this long after the last keystroke in a text field. */
export const NOTICE_TYPING_QUIET_MS = 2500;
/** And this long after Fleet opens, so it never lands on top of the first paint. */
export const NOTICE_STARTUP_QUIET_MS = 3000;

const SHOWN_KEY = "weave:notices-shown";
const SHOWN_LIMIT = 50;

function readShown(): string[] {
  try {
    const parsed: unknown = JSON.parse(localStorage.getItem(SHOWN_KEY) ?? "[]");
    return Array.isArray(parsed) ? parsed.filter((id): id is string => typeof id === "string") : [];
  } catch {
    return [];
  }
}

function writeShown(ids: readonly string[]): void {
  try {
    localStorage.setItem(SHOWN_KEY, JSON.stringify(ids.slice(-SHOWN_LIMIT)));
  } catch {
    // localStorage unavailable: the notice may show again next time, which is the safe way to fail.
  }
}

/**
 * Quiet, one-at-a-time notices. A notice opens as a small card once, then settles into a chip in the status bar that
 * stays until whatever posted it removes it. Fleet remembers which notices it has shown, so the same one never
 * interrupts twice, even across restarts. When a card may open is up to the host (NoticeCard.vue), which waits for a
 * quiet moment.
 */
export const useNoticesStore = defineStore("notices", () => {
  const notices = shallowRef<readonly Notice[]>([]);
  /** Notices waiting to open as a card, oldest first. */
  const waiting = shallowRef<readonly string[]>([]);
  const openId = shallowRef<string | null>(null);
  /** The open card stays until it's closed: the user opened it from its chip, or it's asking them something. */
  const pinned = shallowRef(false);
  const shown = new Set(readShown());
  const expiry = new Map<string, ReturnType<typeof setTimeout>>();

  const open = computed(() => notices.value.find((notice) => notice.id === openId.value) ?? null);
  // A notice waiting for its card has no chip yet: it arrives as a card first.
  const chips = computed(() =>
    notices.value.filter((notice) => notice.chip && notice.id !== openId.value && !waiting.value.includes(notice.id)),
  );
  const nextWaiting = computed(() => waiting.value[0] ?? null);

  function find(id: string): Notice | undefined {
    return notices.value.find((notice) => notice.id === id);
  }

  function markShown(id: string): void {
    if (shown.has(id)) return;
    shown.add(id);
    writeShown([...shown]);
  }

  /** Adds a notice, or updates the one with the same id in place. */
  function post(notice: Notice): void {
    const existing = find(notice.id);
    if (existing) {
      notices.value = notices.value.map((item) => (item.id === notice.id ? notice : item));
      return;
    }
    notices.value = [...notices.value, notice];
    if (notice.expiresMs) expiry.set(notice.id, setTimeout(() => remove(notice.id), notice.expiresMs));
    if (!notice.quiet && !shown.has(notice.id)) waiting.value = [...waiting.value, notice.id];
  }

  /** Changes what a card says without closing it (a confirmation step, for one). An open card then stays open. */
  function update(id: string, content: Partial<NoticeContent>): void {
    notices.value = notices.value.map((item) => (item.id === id ? { ...item, ...content } : item));
    if (openId.value === id) pinned.value = true;
  }

  function has(id: string): boolean {
    return find(id) !== undefined;
  }

  /** Opens the next waiting card. The host calls this when it's a good moment. */
  function openNext(): void {
    const id = nextWaiting.value;
    if (!id || openId.value) return;
    waiting.value = waiting.value.slice(1);
    openId.value = id;
    pinned.value = false;
    markShown(id);
  }

  /** Opens a notice's card again, from its chip or the app menu. It stays open until closed. */
  function reopen(id: string): void {
    if (!find(id)) return;
    waiting.value = waiting.value.filter((item) => item !== id);
    pinned.value = true;
    openId.value = id;
    markShown(id);
  }

  /** Closes the card: it becomes its chip, or goes away if it has none. */
  function settle(id: string | null = openId.value): void {
    if (!id || openId.value !== id) return;
    openId.value = null;
    pinned.value = false;
    const notice = find(id);
    if (notice && !notice.chip) remove(id);
  }

  function remove(id: string): void {
    clearTimeout(expiry.get(id));
    expiry.delete(id);
    notices.value = notices.value.filter((item) => item.id !== id);
    waiting.value = waiting.value.filter((item) => item !== id);
    if (openId.value === id) {
      openId.value = null;
      pinned.value = false;
    }
  }

  /** Removes every notice whose id starts with the prefix, except the one to keep. */
  function removeWhere(prefix: string, keep?: string): void {
    for (const notice of notices.value) {
      if (notice.id.startsWith(prefix) && notice.id !== keep) remove(notice.id);
    }
  }

  return { notices, open, pinned, chips, nextWaiting, has, post, update, openNext, reopen, settle, remove, removeWhere };
});
