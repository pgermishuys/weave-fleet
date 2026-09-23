import { storeToRefs } from "pinia";
import { computed, readonly, shallowRef } from "vue";
import type { components } from "@/api/generated/schema";
import { useDraftState } from "@/composables/use-draft-state";
import { api } from "@/api/client";
import { modelFromKey } from "@/lib/agent-model-choice";
import { useSessionsStore } from "@/stores/sessions";

interface BackendSendCommandRequest {
  command: string;
  arguments?: string;
  agent?: string;
  model?: { providerID: string; modelID: string };
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
      const { error, response } = await api.POST("/api/sessions/{id}/command", {
        params: {
          path: { id: sessionId },
        },
        body: request as components["schemas"]["SendCommandApiRequest"],
      });

      if (error || !response.ok) {
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

    // A pick the harness no longer lists is sent as picked, not swapped for another: the harness says what's wrong.
    const pickedModel = modelFromKey(draft.modelId);

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

    if (draft.agentId) {
      request.agent = draft.agentId;
    }

    if (pickedModel) {
      request.model = pickedModel;
    }

    void postCommand(request);

    return true;
  }

  return {
    error: readonly(sendError),
    sendCommand,
  };
}
