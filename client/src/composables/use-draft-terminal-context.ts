import { computed, reactive, type ComputedRef } from "vue";
import type { TerminalContextDraft } from "@/lib/format-terminal-context";

/**
 * Terminal lines waiting in each session's composer, kept with the rest of
 * the draft for as long as the page is open.
 */
const registry = reactive<Record<string, TerminalContextDraft[]>>({});

function entry(sessionId: string): TerminalContextDraft[] {
  if (!registry[sessionId]) registry[sessionId] = [];
  return registry[sessionId];
}

/** Adds lines to the session's draft. The same lines of the same terminal are only added once. */
export function addDraftTerminalContext(sessionId: string, context: Omit<TerminalContextDraft, "id">): void {
  const list = entry(sessionId);
  const duplicate = list.some((item) =>
    item.terminalId === context.terminalId && item.from === context.from && item.to === context.to);
  if (duplicate) return;
  list.push({ ...context, id: `${context.terminalId}:${context.from}-${context.to}:${Date.now()}` });
}

export function clearDraftTerminalContext(sessionId: string): void {
  registry[sessionId] = [];
}

export function useDraftTerminalContext(sessionId: string) {
  const contexts: ComputedRef<TerminalContextDraft[]> = computed(() => entry(sessionId));

  function removeContext(id: string): void {
    const list = entry(sessionId);
    const index = list.findIndex((item) => item.id === id);
    if (index >= 0) list.splice(index, 1);
  }

  return {
    contexts,
    removeContext,
    clearContexts: () => clearDraftTerminalContext(sessionId),
  };
}
