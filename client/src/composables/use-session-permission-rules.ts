import { computed, onBeforeUnmount, reactive, readonly } from "vue";
import { onReconnect, useWeaveSocket } from "@/composables/use-weave-socket";
import { useMachineTarget } from "@/lib/machine-target";
import type { AllowedRule } from "@/lib/tools";

/** The `session.permission-rules` event: a session's whole list, sent whenever it changes. */
const RULES_EVENT = "session.permission-rules" as const;

/** Each session's rules as Fleet last said, so switching back to a session shows them at once. */
const rulesBySession = reactive<Record<string, AllowedRule[]>>({});

function toRule(value: unknown): AllowedRule | null {
  if (!value || typeof value !== "object") return null;
  const raw = value as Record<string, unknown>;
  if (typeof raw.id !== "string" || typeof raw.tool !== "string") return null;
  const patterns = Array.isArray(raw.patterns) ? raw.patterns.filter((pattern): pattern is string => typeof pattern === "string") : [];
  return {
    id: raw.id,
    kind: typeof raw.kind === "string" ? raw.kind : "other",
    tool: raw.tool,
    patterns,
    createdAt: typeof raw.createdAt === "string" ? raw.createdAt : "",
  };
}

function toRules(values: unknown): AllowedRule[] {
  return Array.isArray(values) ? values.map(toRule).filter((rule): rule is AllowedRule => rule !== null) : [];
}

/**
 * What "Don't ask again" allowed in a session: the rules Fleet keeps for it, to read and to remove. Loaded when the
 * session opens and after a reconnect; `session.permission-rules` on the session's topic keeps every open client current.
 */
export function useSessionPermissionRules(sessionId: string) {
  const machine = useMachineTarget();
  const { subscribeV2 } = useWeaveSocket(machine);
  const rules = computed<readonly AllowedRule[]>(() => rulesBySession[sessionId] ?? []);
  let loadId = 0;

  async function load(): Promise<void> {
    const current = ++loadId;
    try {
      const { data, response } = await machine.api.GET("/api/sessions/{id}/permission-rules", { params: { path: { id: sessionId } } });
      if (current !== loadId || !response.ok) return;
      rulesBySession[sessionId] = toRules(data);
    } catch (loadError) {
      console.warn(`Failed to load the permission rules of session ${sessionId}:`, loadError);
    }
  }

  const unsubscribe = subscribeV2(
    `session:${sessionId}`,
    () => {
      // The rules load from their own endpoint; snapshots don't carry them.
    },
    (event) => {
      if (event.type !== RULES_EVENT || event.payload?.sessionId !== sessionId) return;
      // Newer than any load in flight.
      loadId += 1;
      rulesBySession[sessionId] = toRules(event.payload.items);
    },
  );
  void load();
  const stopReconnect = onReconnect(machine, () => void load());

  onBeforeUnmount(() => {
    unsubscribe();
    stopReconnect();
  });

  /** Removes one rule: it goes at once, and the next ask it covered asks again. If Fleet refuses, the list loads again. */
  async function remove(ruleId: string): Promise<void> {
    rulesBySession[sessionId] = (rulesBySession[sessionId] ?? []).filter((rule) => rule.id !== ruleId);
    try {
      const { response } = await machine.api.DELETE("/api/sessions/{id}/permission-rules/{ruleId}", { params: { path: { id: sessionId, ruleId } } });
      if (!response.ok && response.status !== 404) await load();
    } catch {
      await load();
    }
  }

  /** Removes every rule of the session. */
  async function removeAll(): Promise<void> {
    rulesBySession[sessionId] = [];
    try {
      const { response } = await machine.api.DELETE("/api/sessions/{id}/permission-rules", { params: { path: { id: sessionId } } });
      if (!response.ok && response.status !== 404) await load();
    } catch {
      await load();
    }
  }

  return { rules: readonly(rules), remove, removeAll };
}
