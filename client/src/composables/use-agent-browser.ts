import { onBeforeUnmount, toValue, watch, type MaybeRefOrGetter } from "vue";
import { apiFetchOn } from "@/lib/api-client";
import { useMachineTarget, type MachineTarget } from "@/lib/machine-target";
import { isBrowserStepEvent } from "@/lib/domain-events";
import { useAgentBrowserStore, type AgentBrowserState } from "@/stores/agent-browser";
import { onReconnect, useWeaveSocket } from "@/composables/use-weave-socket";

/** The agent's tabs and steps for the session, as Fleet has them. */
export async function fetchAgentBrowser(machine: MachineTarget, sessionId: string): Promise<AgentBrowserState> {
  const response = await apiFetchOn(machine.connection, `/api/sessions/${encodeURIComponent(sessionId)}/agent-browser`);
  if (!response.ok) throw new Error(`HTTP ${response.status}`);
  const body = (await response.json()) as Partial<AgentBrowserState>;
  return { tabs: body.tabs ?? [], focusedTabId: body.focusedTabId ?? null, steps: body.steps ?? [] };
}

/** Where a picture of the agent's tab comes from, for Agent's view. `nonce` makes each request a fresh one. */
export function agentFrameUrl(sessionId: string, tabId: string, nonce: number): string {
  return `/api/sessions/${encodeURIComponent(sessionId)}/agent-browser/tabs/${encodeURIComponent(tabId)}/frame?n=${nonce}`;
}

/**
 * Keeps the session's agent browser (tabs and steps) in the store: loads it when the session opens and on reconnect,
 * and applies `browser.step` events from the session topic. Steps aren't replayed after a disconnect, so a reconnect
 * loads them again.
 */
export function useAgentBrowser(sessionId: MaybeRefOrGetter<string | null | undefined>): void {
  const machine = useMachineTarget();
  const store = useAgentBrowserStore();
  const { subscribeV2 } = useWeaveSocket();

  async function load(id: string): Promise<void> {
    try {
      store.setFromServer(id, await fetchAgentBrowser(machine, id));
    } catch (error) {
      console.warn(`Failed to load the agent's browser for session ${id}:`, error);
    }
  }

  watch(
    () => toValue(sessionId) ?? "",
    (id, _previous, onCleanup) => {
      if (!id) return;
      const unsubscribe = subscribeV2(
        `session:${id}`,
        () => {
          // Steps load from the REST endpoint; snapshots don't carry them.
        },
        (event) => {
          if (isBrowserStepEvent(event) && event.payload.sessionId === id) store.applyStep(event.payload);
        },
      );
      void load(id);
      onCleanup(unsubscribe);
    },
    { immediate: true },
  );

  const stopReconnect = onReconnect(() => {
    const id = toValue(sessionId);
    if (id) void load(id);
  });
  onBeforeUnmount(stopReconnect);
}
