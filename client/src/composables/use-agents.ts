import { storeToRefs } from "pinia";
import { computed, readonly, ref, shallowRef, watch } from "vue";
import { apiFetch } from "@/lib/api-client";
import type { AutocompleteAgent } from "@/lib/api-types";
import { useSessionsStore } from "@/stores/sessions";

export interface AgentOption {
  id: string;
  name: string;
  description: string;
}

function toAgentOptions(agents: readonly AutocompleteAgent[]): AgentOption[] {
  return agents
    .filter((agent) => !agent.hidden)
    .map((agent) => ({
      id: agent.name,
      name: agent.name,
      description: agent.description ?? "",
    }));
}

export function useAgents(instanceId?: string) {
  const sessionsStore = useSessionsStore();
  const { sessions, activeSessionId } = storeToRefs(sessionsStore);

  const agents = ref<AgentOption[]>([]);
  const agentsById = ref<Record<string, AgentOption>>({});
  const isLoading = shallowRef(false);
  const error = shallowRef<string | undefined>(undefined);

  const resolvedInstanceId = computed(() => {
    if (instanceId) {
      return instanceId;
    }

    const activeSession = sessions.value.find((session) => session.session.id === activeSessionId.value);
    return activeSession?.instanceId ?? "";
  });
  const defaultAgentId = computed(() => agents.value[0]?.id ?? "");

  watch(
    resolvedInstanceId,
    async (nextInstanceId, _previous, onCleanup) => {
      if (!nextInstanceId) {
        agents.value = [];
        agentsById.value = {};
        isLoading.value = false;
        error.value = undefined;
        return;
      }

      const controller = new AbortController();
      onCleanup(() => {
        controller.abort();
      });

      isLoading.value = true;
      error.value = undefined;

      try {
        const response = await apiFetch(`/api/instances/${encodeURIComponent(nextInstanceId)}/agents`, {
          signal: controller.signal,
        });

        if (!response.ok) {
          const payload = (await response.json().catch(() => ({}))) as { error?: string };
          throw new Error(payload.error ?? `HTTP ${response.status}`);
        }

        const body = (await response.json()) as { agents?: AutocompleteAgent[] } | AutocompleteAgent[];
        const nextAgents = toAgentOptions(Array.isArray(body) ? body : body.agents ?? []);

        agents.value = nextAgents;
        agentsById.value = Object.fromEntries(nextAgents.map((agent) => [agent.id, agent])) as Record<string, AgentOption>;
      } catch (fetchError) {
        if (fetchError instanceof DOMException && fetchError.name === "AbortError") {
          return;
        }

        agents.value = [];
        agentsById.value = {};
        error.value = fetchError instanceof Error ? fetchError.message : "Failed to load agents";
      } finally {
        isLoading.value = false;
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
