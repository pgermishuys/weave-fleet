import { defineStore } from "pinia";
import { shallowRef } from "vue";
import type { BrowserStep } from "@/lib/domain-events";

/**
 * The agent's own browser per session: its open tabs and the steps it took. Loaded with the session and kept current by
 * `browser.step` events (see use-agent-browser.ts). The conversation lists a call's steps under it; a browser canvas
 * showing the same page offers Agent's view of the tab.
 */

/** A tab in the agent's own browser (`AgentTab` on the server). */
export interface AgentTab {
  id: string;
  url: string;
  title: string;
  loading: boolean;
  loadError?: string | null;
  canGoBack: boolean;
  canGoForward: boolean;
  generation: number;
}

export interface AgentBrowserState {
  tabs: AgentTab[];
  focusedTabId: string | null;
  steps: BrowserStep[];
}

const EMPTY: AgentBrowserState = Object.freeze({ tabs: [], focusedTabId: null, steps: [] }) as AgentBrowserState;

/** The same origin (scheme, host, port), so a canvas on `localhost:5173` matches the agent's `127.0.0.1:5173` too. */
export function sameApp(a: string, b: string): boolean {
  try {
    const x = new URL(a);
    const y = new URL(b);
    const loopback = (host: string) => host === "localhost" || host === "127.0.0.1" || host === "[::1]" || host.endsWith(".localhost");
    const host = (u: URL) => (loopback(u.hostname) ? "loopback" : u.hostname);
    return x.protocol === y.protocol && host(x) === host(y) && (x.port || "80") === (y.port || "80");
  } catch {
    return false;
  }
}

export const useAgentBrowserStore = defineStore("agent-browser", () => {
  const sessions = shallowRef<Record<string, AgentBrowserState>>({});

  function of(sessionId: string): AgentBrowserState {
    return sessions.value[sessionId] ?? EMPTY;
  }

  function set(sessionId: string, state: AgentBrowserState): void {
    sessions.value = { ...sessions.value, [sessionId]: state };
  }

  /** What Fleet said when the session loaded; steps that arrived meanwhile stay. */
  function setFromServer(sessionId: string, loaded: AgentBrowserState): void {
    const seen = new Set(loaded.steps.map((step) => step.seq));
    const later = of(sessionId).steps.filter((step) => !seen.has(step.seq));
    set(sessionId, { tabs: loaded.tabs, focusedTabId: loaded.focusedTabId, steps: [...loaded.steps, ...later].sort((a, b) => a.seq - b.seq) });
  }

  /** A new step: added once, and the tab it was on updated from it (opened, moved, closed). */
  function applyStep(step: BrowserStep): void {
    const current = of(step.sessionId);
    if (current.steps.some((known) => known.seq === step.seq)) return;
    let tabs = current.tabs;
    let focusedTabId = current.focusedTabId;
    if (step.tabId && step.ok) {
      if (step.kind === "tabs.close") {
        tabs = tabs.filter((tab) => tab.id !== step.tabId);
        if (focusedTabId === step.tabId) focusedTabId = tabs.at(-1)?.id ?? null;
      } else if (step.url) {
        const existing = tabs.find((tab) => tab.id === step.tabId);
        const updated: AgentTab = {
          id: step.tabId,
          url: step.url,
          title: step.title ?? existing?.title ?? "",
          loading: false,
          loadError: existing?.loadError ?? null,
          canGoBack: existing?.canGoBack ?? false,
          canGoForward: existing?.canGoForward ?? false,
          generation: existing?.generation ?? 0,
        };
        tabs = existing ? tabs.map((tab) => (tab.id === step.tabId ? updated : tab)) : [...tabs, updated];
        if (step.kind === "tabs.open" || step.kind === "tabs.focus") focusedTabId = step.tabId;
      }
    }
    set(step.sessionId, { tabs, focusedTabId, steps: [...current.steps, step] });
  }

  /** The steps a tool call took, oldest first. */
  function stepsOf(sessionId: string, callId: string | undefined): BrowserStep[] {
    if (!callId) return [];
    return of(sessionId).steps.filter((step) => step.callId === callId);
  }

  /** The agent's tab on the same app as `url`, when it has one open. */
  function tabFor(sessionId: string, url: string | null | undefined): AgentTab | null {
    if (!url) return null;
    const { tabs, focusedTabId } = of(sessionId);
    const matching = tabs.filter((tab) => sameApp(tab.url, url));
    return matching.find((tab) => tab.id === focusedTabId) ?? matching.at(-1) ?? null;
  }

  /** The newest step taken on `tabId`. */
  function lastStepOn(sessionId: string, tabId: string): BrowserStep | null {
    const steps = of(sessionId).steps;
    for (let i = steps.length - 1; i >= 0; i--) {
      if (steps[i]!.tabId === tabId) return steps[i]!;
    }
    return null;
  }

  return { sessions, of, setFromServer, applyStep, stepsOf, tabFor, lastStepOn };
});
