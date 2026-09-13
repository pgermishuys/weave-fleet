import { defineStore } from "pinia";
import { ref } from "vue";
import type { TerminalEvent } from "@/lib/domain-events";
import type { TerminalSummary } from "@/lib/terminal-api";

/**
 * Terminal tabs per session, which one is showing, and whether the session's
 * drawer is open. The drawer's height is one value for the whole window.
 */

export const DEFAULT_DRAWER_HEIGHT = 272;
export const MIN_DRAWER_HEIGHT = 140;

const HEIGHT_STORAGE_KEY = "weave:terminal-height";
const OPEN_STORAGE_KEY = "weave:terminal-open";

interface SessionTerminals {
  terminals: TerminalSummary[];
  activeId: string | null;
}

function readHeight(): number {
  try {
    const stored = Number(window.localStorage.getItem(HEIGHT_STORAGE_KEY));
    return Number.isFinite(stored) && stored >= MIN_DRAWER_HEIGHT ? stored : DEFAULT_DRAWER_HEIGHT;
  } catch {
    return DEFAULT_DRAWER_HEIGHT;
  }
}

function readOpenSessions(): string[] {
  try {
    const value: unknown = JSON.parse(window.localStorage.getItem(OPEN_STORAGE_KEY) ?? "[]");
    return Array.isArray(value) ? value.filter((id): id is string => typeof id === "string") : [];
  } catch {
    return [];
  }
}

function persist(key: string, value: string): void {
  try {
    window.localStorage.setItem(key, value);
  } catch {
    // localStorage unavailable
  }
}

export const useTerminalsStore = defineStore("terminals", () => {
  const sessions = ref<Record<string, SessionTerminals>>({});
  const loadedSessions = ref<string[]>([]);
  const openSessions = ref<string[]>(readOpenSessions());
  const height = ref(readHeight());
  /** True while a terminal has the keyboard, so the status bar can say so. */
  const focused = ref(false);

  function entry(sessionId: string): SessionTerminals {
    let current = sessions.value[sessionId];
    if (!current) {
      sessions.value[sessionId] = { terminals: [], activeId: null };
      current = sessions.value[sessionId];
    }
    return current;
  }

  function terminalsFor(sessionId: string): TerminalSummary[] {
    return sessions.value[sessionId]?.terminals ?? [];
  }

  function activeFor(sessionId: string): TerminalSummary | null {
    const current = sessions.value[sessionId];
    return current?.terminals.find((terminal) => terminal.id === current.activeId) ?? null;
  }

  /** Replaces the session's tabs with what the server listed, keeping the active tab when it's still there. */
  function setTerminals(sessionId: string, list: TerminalSummary[]): void {
    if (!loadedSessions.value.includes(sessionId)) loadedSessions.value = [...loadedSessions.value, sessionId];
    const current = entry(sessionId);
    current.terminals = [...list];
    if (!list.some((terminal) => terminal.id === current.activeId)) current.activeId = list[0]?.id ?? null;
  }

  /** Adds or updates a tab. A new tab shows when asked to, or when nothing else is showing. */
  function add(sessionId: string, terminal: TerminalSummary, activate: boolean): void {
    const current = entry(sessionId);
    const index = current.terminals.findIndex((candidate) => candidate.id === terminal.id);
    if (index >= 0) current.terminals[index] = terminal;
    else current.terminals.push(terminal);
    if (activate || current.activeId === null) current.activeId = terminal.id;
  }

  /** Removes a tab; the one next to it shows instead. */
  function remove(sessionId: string, terminalId: string): void {
    const current = sessions.value[sessionId];
    if (!current) return;
    const index = current.terminals.findIndex((terminal) => terminal.id === terminalId);
    if (index < 0) return;
    current.terminals.splice(index, 1);
    if (current.activeId === terminalId) {
      current.activeId = current.terminals[Math.min(index, current.terminals.length - 1)]?.id ?? null;
    }
  }

  function setActive(sessionId: string, terminalId: string): void {
    const current = sessions.value[sessionId];
    if (current?.terminals.some((terminal) => terminal.id === terminalId)) current.activeId = terminalId;
  }

  /** A tab opened or closed elsewhere (another window, or the shell ended). */
  function applyEvent(event: TerminalEvent): void {
    const { sessionId, terminalId, title } = event.payload;
    if (event.type === "terminal.opened") {
      if (terminalsFor(sessionId).some((terminal) => terminal.id === terminalId)) return;
      add(sessionId, { id: terminalId, title, status: "running", createdAt: new Date().toISOString() }, false);
      return;
    }
    remove(sessionId, terminalId);
  }

  /** Whether the session's list has come from the server at least once. */
  function isLoaded(sessionId: string): boolean {
    return loadedSessions.value.includes(sessionId);
  }

  function setFocused(value: boolean): void {
    focused.value = value;
  }

  function isOpen(sessionId: string): boolean {
    return openSessions.value.includes(sessionId);
  }

  function setOpen(sessionId: string, open: boolean): void {
    if (open === isOpen(sessionId)) return;
    openSessions.value = open
      ? [...openSessions.value, sessionId]
      : openSessions.value.filter((id) => id !== sessionId);
    persist(OPEN_STORAGE_KEY, JSON.stringify(openSessions.value));
  }

  function toggleOpen(sessionId: string): void {
    setOpen(sessionId, !isOpen(sessionId));
  }

  function setHeight(value: number): void {
    height.value = Math.max(MIN_DRAWER_HEIGHT, Math.round(value));
    persist(HEIGHT_STORAGE_KEY, String(height.value));
  }

  function forgetSession(sessionId: string): void {
    delete sessions.value[sessionId];
    loadedSessions.value = loadedSessions.value.filter((id) => id !== sessionId);
    setOpen(sessionId, false);
  }

  return {
    sessions,
    height,
    focused,
    isLoaded,
    setFocused,
    terminalsFor,
    activeFor,
    setTerminals,
    add,
    remove,
    setActive,
    applyEvent,
    isOpen,
    setOpen,
    toggleOpen,
    setHeight,
    forgetSession,
  };
});
