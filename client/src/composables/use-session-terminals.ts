import { onBeforeUnmount, toValue, watch, type MaybeRefOrGetter } from "vue";
import { isTerminalEvent, type TerminalEvent } from "@/lib/domain-events";
import { closeTerminal, createTerminal, listTerminals, TerminalApiError } from "@/lib/terminal-api";
import { useAppShellStore } from "@/stores/app-shell";
import { useTerminalsStore } from "@/stores/terminals";
import { onReconnect, useWeaveSocket } from "@/composables/use-weave-socket";

/**
 * Opens a new terminal in the session and shows it. Returns the error message
 * to show the user, or null when it worked.
 */
export async function openNewTerminal(sessionId: string, cols: number, rows: number): Promise<string | null> {
  const store = useTerminalsStore();
  try {
    const terminal = await createTerminal(sessionId, cols, rows);
    store.add(sessionId, terminal, true);
    store.setOpen(sessionId, true);
    return null;
  } catch (error) {
    return error instanceof TerminalApiError ? error.message : "Couldn't reach Fleet to open a terminal.";
  }
}

/**
 * Closes a terminal tab: it goes at once, and the server ends its shell. If
 * the server refuses, the session's tabs are loaded again so it comes back.
 */
export async function closeTerminalTab(sessionId: string, terminalId: string): Promise<void> {
  const store = useTerminalsStore();
  store.remove(sessionId, terminalId);
  if (store.terminalsFor(sessionId).length === 0) store.setOpen(sessionId, false);

  try {
    await closeTerminal(sessionId, terminalId);
  } catch (error) {
    console.warn(`Failed to close terminal ${terminalId}:`, error);
    try {
      store.setTerminals(sessionId, await listTerminals(sessionId));
    } catch {
      // The next session switch or reconnect loads them again.
    }
  }
}

/**
 * Keeps the active session's terminal tabs in the terminals store: loads them
 * when the session opens and after a reconnect, and applies `terminal.opened`
 * and `terminal.closed` from the session topic. Does nothing when Fleet has
 * terminals turned off.
 */
export function useSessionTerminals(sessionId: MaybeRefOrGetter<string | null | undefined>): void {
  const store = useTerminalsStore();
  const appShell = useAppShellStore();
  const { subscribeV2 } = useWeaveSocket();

  // Events that arrive while the list is loading are replayed on top of it.
  let loading: { sessionId: string; buffered: TerminalEvent[] } | null = null;
  let loadId = 0;

  async function load(id: string): Promise<void> {
    const current = ++loadId;
    const pending = { sessionId: id, buffered: [] as TerminalEvent[] };
    loading = pending;

    try {
      const list = await listTerminals(id);
      if (current !== loadId) return;
      store.setTerminals(id, list);
      for (const event of pending.buffered) store.applyEvent(event);
      if (list.length === 0) store.setOpen(id, false);
    } catch (error) {
      console.warn(`Failed to load terminals for session ${id}:`, error);
    } finally {
      if (loading === pending) loading = null;
    }
  }

  watch(
    () => (appShell.config.terminalEnabled ? (toValue(sessionId) ?? "") : ""),
    (id, _previous, onCleanup) => {
      if (!id) return;

      const unsubscribe = subscribeV2(
        `session:${id}`,
        () => {
          // Terminals load from the REST endpoint; snapshots don't carry them.
        },
        (event) => {
          if (!isTerminalEvent(event) || event.payload.sessionId !== id) return;
          store.applyEvent(event);
          if (event.type === "terminal.closed" && store.terminalsFor(id).length === 0) store.setOpen(id, false);
          if (loading?.sessionId === id) loading.buffered.push(event);
        },
      );
      void load(id);

      onCleanup(() => {
        unsubscribe();
        loadId += 1;
        loading = null;
      });
    },
    { immediate: true },
  );

  const stopReconnect = onReconnect(() => {
    const id = toValue(sessionId);
    if (id && appShell.config.terminalEnabled) void load(id);
  });

  onBeforeUnmount(stopReconnect);
}
