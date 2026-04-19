import { storeToRefs } from "pinia";
import { computed, reactive } from "vue";
import { useAgents } from "@/composables/use-agents";
import { useDraftState, type EffortLevel } from "@/composables/use-draft-state";
import { useModels } from "@/composables/use-models";
import { apiFetch } from "@/lib/api-client";
import type { AccumulatedMessage } from "@/lib/api-types";
import { useSessionsStore } from "@/stores/sessions";

export interface SentPromptMessage {
  id: string;
  body: string;
  timestamp: string;
  agentId: string;
  agentName: string;
  modelId: string;
  modelName: string;
  effort: EffortLevel;
}

const sentPromptRegistry = reactive<Record<string, SentPromptMessage[]>>({});
const pendingPromptRegistry = reactive<Record<string, number>>({});

function ensureSentPrompts(sessionId: string): SentPromptMessage[] {
  const existingPrompts = sentPromptRegistry[sessionId];
  if (existingPrompts) {
    return existingPrompts;
  }

  const prompts: SentPromptMessage[] = [];
  sentPromptRegistry[sessionId] = prompts;
  return prompts;
}

function ensurePendingPromptCount(sessionId: string): number {
  const existingCount = pendingPromptRegistry[sessionId];
  if (typeof existingCount === "number") {
    return existingCount;
  }

  pendingPromptRegistry[sessionId] = 0;
  return 0;
}

function removeSentPrompt(sessionId: string, promptId: string): void {
  const prompts = sentPromptRegistry[sessionId];
  if (!prompts) {
    return;
  }

  const promptIndex = prompts.findIndex((prompt) => prompt.id === promptId);
  if (promptIndex >= 0) {
    prompts.splice(promptIndex, 1);
  }
}

export function clearSentPrompts(sessionId: string): void {
  delete sentPromptRegistry[sessionId];
}

export function incrementPendingPrompts(sessionId: string): void {
  pendingPromptRegistry[sessionId] = (pendingPromptRegistry[sessionId] ?? 0) + 1;
}

export function decrementPendingPrompts(sessionId: string): void {
  const nextCount = (pendingPromptRegistry[sessionId] ?? 0) - 1;
  if (nextCount > 0) {
    pendingPromptRegistry[sessionId] = nextCount;
    return;
  }

  delete pendingPromptRegistry[sessionId];
}

export function clearPendingPrompts(sessionId: string): void {
  delete pendingPromptRegistry[sessionId];
}

function buildDeliveredPromptCounts(messages: readonly AccumulatedMessage[]): Map<string, number> {
  const deliveredPromptCounts = new Map<string, number>();

  for (const message of messages) {
    if (message.role !== "user") {
      continue;
    }

    const text = message.parts
      .filter((part): part is Extract<AccumulatedMessage["parts"][number], { type: "text" }> => part.type === "text")
      .map((part) => part.text)
      .join("\n\n")
      .trim();

    if (!text) {
      continue;
    }

    deliveredPromptCounts.set(text, (deliveredPromptCounts.get(text) ?? 0) + 1);
  }

  return deliveredPromptCounts;
}

export function reconcileSentPrompts(sessionId: string, messages: readonly AccumulatedMessage[]): void {
  const prompts = sentPromptRegistry[sessionId];
  if (!prompts || prompts.length === 0) {
    return;
  }

  const deliveredPromptCounts = buildDeliveredPromptCounts(messages);
  let unmatchedDeliveredPromptCount = 0;

  for (const message of messages) {
    if (message.role === "user") {
      unmatchedDeliveredPromptCount += 1;
    }
  }

  const remainingPrompts = prompts.filter((prompt) => {
    const deliveredCount = deliveredPromptCounts.get(prompt.body) ?? 0;
    if (deliveredCount > 0) {
      deliveredPromptCounts.set(prompt.body, deliveredCount - 1);
      unmatchedDeliveredPromptCount -= 1;
      return false;
    }

    if (unmatchedDeliveredPromptCount > 0) {
      unmatchedDeliveredPromptCount -= 1;
      return false;
    }

    return true;
  });

  if (remainingPrompts.length === prompts.length) {
    return;
  }

  if (remainingPrompts.length === 0) {
    delete sentPromptRegistry[sessionId];
    return;
  }

  sentPromptRegistry[sessionId] = remainingPrompts;
}

export function useSentPrompts(sessionId: string) {
  ensureSentPrompts(sessionId);
  ensurePendingPromptCount(sessionId);

  const sentPrompts = computed(() => [...(sentPromptRegistry[sessionId] ?? [])]);
  const hasPendingPrompts = computed(() => (pendingPromptRegistry[sessionId] ?? 0) > 0);

  return {
    sentPrompts,
    hasPendingPrompts,
  };
}

function formatTimestamp(date: Date): string {
  return new Intl.DateTimeFormat("en-US", {
    hour: "numeric",
    minute: "2-digit",
  }).format(date);
}

interface BackendSendPromptRequest {
  text: string;
  agent?: string;
  model?: string;
}

export function useSendPrompt(sessionId: string, _instanceId?: string) {
  const sessionsStore = useSessionsStore();
  const { sessions } = storeToRefs(sessionsStore);
  const { defaultAgentId, agentsById } = useAgents();
  const { defaultModelId, modelsById } = useModels(sessionId);
  const { draft, resetText } = useDraftState(sessionId, {
    agentId: "",
    modelId: "",
  });

  const selectedSession = computed(() => {
    return sessions.value.find((session) => session.session.id === sessionId) ?? null;
  });

  const sentPrompts = computed(() => sentPromptRegistry[sessionId] ?? []);
  const canSend = computed(() => draft.text.trim().length > 0);

  async function postPrompt(promptId: string, request: BackendSendPromptRequest): Promise<void> {
    try {
      const response = await apiFetch(`/api/sessions/${encodeURIComponent(sessionId)}/prompt`, {
        method: "POST",
        headers: {
          "Content-Type": "application/json",
        },
        body: JSON.stringify(request),
      });

      if (!response.ok) {
        throw new Error(`HTTP ${response.status}`);
      }
    } catch (error) {
      removeSentPrompt(sessionId, promptId);
      decrementPendingPrompts(sessionId);
      console.error("Failed to send prompt", error);
    }
  }

  function sendPrompt(_overrideInstanceId?: string): boolean {
    const body = draft.text.trim();
    if (!body) {
      return false;
    }

    const agent = agentsById.value[draft.agentId] ?? agentsById.value[defaultAgentId.value];
    const model = modelsById.value[draft.modelId] ?? modelsById.value[defaultModelId.value];
    const now = new Date();
    const promptId = `${sessionId}-${now.getTime()}`;
    const resolvedAgentId = agent?.id ?? draft.agentId ?? defaultAgentId.value;
    const resolvedModelId = model?.id ?? defaultModelId.value;
    const usesDefaultAgent = !draft.agentId;
    const usesDefaultModel = !draft.modelId;

    ensureSentPrompts(sessionId).push({
      id: promptId,
      body,
      timestamp: formatTimestamp(now),
      agentId: resolvedAgentId,
      agentName: agent?.name ?? "Unknown agent",
      modelId: resolvedModelId,
      modelName: model?.name ?? "Unknown model",
      effort: draft.effort,
    });
    incrementPendingPrompts(sessionId);

    if (selectedSession.value) {
      selectedSession.value.activityStatus = "busy";
      selectedSession.value.lifecycleStatus = "running";
      selectedSession.value.sessionStatus = "active";
    }

    resetText();
    const request: BackendSendPromptRequest = {
      text: body,
    };

    if (resolvedAgentId && !usesDefaultAgent) {
      request.agent = resolvedAgentId;
    }

    if (resolvedModelId && !usesDefaultModel) {
      request.model = resolvedModelId;
    }

    void postPrompt(promptId, request);

    return true;
  }

  return {
    canSend,
    sendPrompt,
    sentPrompts,
  };
}
