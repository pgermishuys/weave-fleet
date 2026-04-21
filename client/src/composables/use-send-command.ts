import { storeToRefs } from "pinia";
import { computed, readonly, shallowRef } from "vue";
import { useAgents } from "@/composables/use-agents";
import { useDraftState } from "@/composables/use-draft-state";
import { useModels } from "@/composables/use-models";
import { apiFetch } from "@/lib/api-client";
import { useSessionsStore } from "@/stores/sessions";

interface BackendSendCommandRequest {
  command: string;
  arguments?: string;
  agent?: string;
  model?: string;
}

async function readCommandErrorMessage(response: Response): Promise<string> {
  const bodyText = await response.text().catch(() => "");
  if (!bodyText) {
    return `HTTP ${response.status}`;
  }

  try {
    const body = JSON.parse(bodyText) as Record<string, unknown>;

    if (typeof body.error === "string" && body.error.trim().length > 0) {
      return body.error;
    }

    if (typeof body.detail === "string" && body.detail.trim().length > 0) {
      return body.detail;
    }

    if (typeof body.title === "string" && body.title.trim().length > 0) {
      return body.title;
    }
  } catch {
    if (bodyText.trim().length > 0) {
      return bodyText.trim();
    }
  }

  return `HTTP ${response.status}`;
}

export function useSendCommand(sessionId: string) {
  const sessionsStore = useSessionsStore();
  const { sessions } = storeToRefs(sessionsStore);
  const { defaultAgentId, agentsById } = useAgents();
  const { defaultModelId, modelsById } = useModels(sessionId);
  const sendError = shallowRef<string | undefined>(undefined);
  const { draft, resetText } = useDraftState(sessionId, {
    agentId: "",
    modelId: "",
  });

  const selectedSession = computed(() => {
    return sessions.value.find((session) => session.session.id === sessionId) ?? null;
  });

  async function postCommand(request: BackendSendCommandRequest): Promise<void> {
    try {
      const response = await apiFetch(`/api/sessions/${encodeURIComponent(sessionId)}/command`, {
        method: "POST",
        headers: {
          "Content-Type": "application/json",
        },
        body: JSON.stringify(request),
      });

      if (!response.ok) {
        throw new Error(await readCommandErrorMessage(response));
      }

      sendError.value = undefined;
    } catch (caughtError) {
      const message = caughtError instanceof Error ? caughtError.message : "Failed to send command";
      sendError.value = message;
      console.error("Failed to send command", caughtError);
    }
  }

  function sendCommand(command: string, args = ""): boolean {
    const trimmedCommand = command.trim();
    if (!trimmedCommand) {
      return false;
    }

    sendError.value = undefined;

    const agent = agentsById.value[draft.agentId] ?? agentsById.value[defaultAgentId.value];
    const model = modelsById.value[draft.modelId] ?? modelsById.value[defaultModelId.value];
    const resolvedAgentId = agent?.id ?? draft.agentId ?? defaultAgentId.value;
    const resolvedModelId = model?.id ?? defaultModelId.value;
    const usesDefaultAgent = !draft.agentId;
    const usesDefaultModel = !draft.modelId;

    if (selectedSession.value) {
      selectedSession.value.activityStatus = "busy";
      selectedSession.value.lifecycleStatus = "running";
      selectedSession.value.sessionStatus = "active";
    }

    resetText();
    const request: BackendSendCommandRequest = {
      command: trimmedCommand,
    };

    if (args.trim().length > 0) {
      request.arguments = args.trim();
    }

    if (resolvedAgentId && !usesDefaultAgent) {
      request.agent = resolvedAgentId;
    }

    if (resolvedModelId && !usesDefaultModel) {
      request.model = resolvedModelId;
    }

    void postCommand(request);

    return true;
  }

  return {
    error: readonly(sendError),
    sendCommand,
  };
}
