import { storeToRefs } from "pinia";
import { computed, readonly, ref, shallowRef, watch } from "vue";
import { api } from "@/api/client";
import type { AutocompleteAgent, ModelReference } from "@/api/client";
import { sessionCatalogChanges } from "@/lib/harness-catalog-changes";
import { shareInFlight } from "@/lib/shared-request";
import { useSessionsStore } from "@/stores/sessions";

export interface AgentOption {
  id: string;
  name: string;
  description: string;
  /** The agent's own model, when it has one. */
  model?: ModelReference;
}

export function toAgentOptions(agents: readonly AutocompleteAgent[]): AgentOption[] {
  return agents
    .filter((agent) => !agent.hidden)
    .map((agent) => ({
      id: agent.name,
      name: agent.name,
      description: agent.description ?? "",
      ...(agent.model?.providerID && agent.model.modelID ? { model: agent.model } : {}),
    }));
}

/**
 * The agents a session's harness offers, as the harness lists them (hidden ones too). The agent picker and the `@`
 * suggestions share it, so opening a session, or a pushed change to what it offers, asks once.
 */
export const loadSessionAgentList = shareInFlight(async (sessionId: string): Promise<AutocompleteAgent[]> => {
  const { data, error, response } = await api.GET("/api/sessions/{id}/agents", {
    params: { path: { id: sessionId } },
  });

  if (error || !response.ok) {
    const payload = error as { error?: string } | undefined;
    throw new Error(payload?.error ?? `HTTP ${response.status}`);
  }

  const body = data as unknown as { agents?: AutocompleteAgent[] } | AutocompleteAgent[];
  return Array.isArray(body) ? body : body.agents ?? [];
});

export function useAgents(sessionId?: string) {
  const sessionsStore = useSessionsStore();
  const { activeSessionId } = storeToRefs(sessionsStore);

  const agents = ref<AgentOption[]>([]);
  const agentsById = ref<Record<string, AgentOption>>({});
  const isLoading = shallowRef(false);
  const error = shallowRef<string | undefined>(undefined);

  const resolvedSessionId = computed(() => sessionId ?? activeSessionId.value ?? "");
  const defaultAgentId = computed(() => agents.value[0]?.id ?? "");
  // The harness says what the session's folder offers changed (an agent file added): ask again.
  const changes = sessionCatalogChanges(resolvedSessionId);

  watch(
    [resolvedSessionId, changes],
    async ([nextSessionId], previous, onCleanup) => {
      if (!nextSessionId) {
        agents.value = [];
        agentsById.value = {};
        isLoading.value = false;
        error.value = undefined;
        return;
      }

      // The request is shared with every other caller, so leaving only drops its answer.
      let left = false;
      onCleanup(() => {
        left = true;
      });

      // Asked again because the harness's list changed: the old one stays up meanwhile.
      const isRefresh = previous?.[0] === nextSessionId;
      if (!isRefresh) {
        isLoading.value = true;
      }
      error.value = undefined;

      try {
        const nextAgents = toAgentOptions(await loadSessionAgentList(nextSessionId));
        if (left) {
          return;
        }

        agents.value = nextAgents;
        agentsById.value = Object.fromEntries(nextAgents.map((agent) => [agent.id, agent])) as Record<string, AgentOption>;
      } catch (fetchError) {
        if (left) {
          return;
        }

        // A refresh that fails keeps the list it had.
        if (!isRefresh) {
          agents.value = [];
          agentsById.value = {};
        }
        error.value = fetchError instanceof Error ? fetchError.message : "Failed to load agents";
      } finally {
        if (!left) {
          isLoading.value = false;
        }
      }
    },
    { immediate: true },
  );

  return {
    agents: readonly(agents),
    agentsById: readonly(agentsById),
    defaultAgentId,
    isLoading: readonly(isLoading),
    error: readonly(error),
  };
}
