import { computed, readonly, shallowRef } from "vue";
import type { components } from "@/api/generated/schema";
import { useDraftState } from "@/composables/use-draft-state";
import { readPromptErrorMessage, type SessionStatusBeforeSend } from "@/composables/use-send-prompt";
import { modelFromKey } from "@/lib/agent-model-choice";
import { useSessionsStore } from "@/stores/sessions";
import { useMachineTarget } from "@/lib/machine-target";

interface BackendSendCommandRequest {
  command: string;
  arguments?: string;
  agent?: string;
  model?: { providerID: string; modelID: string };
}

export function useSendCommand(sessionId: string) {
  const { api } = useMachineTarget();
  const sessionsStore = useSessionsStore();
  const sendError = shallowRef<string | undefined>(undefined);
  const { draft, setText, resetText } = useDraftState(sessionId, {
    agentId: "",
    modelId: "",
  });

  const selectedSession = computed(() => {
    return sessionsStore.sessionById(sessionId);
  });

  async function postCommand(request: BackendSendCommandRequest, typed: string, before?: SessionStatusBeforeSend): Promise<void> {
    try {
      const { error, response } = await api.POST("/api/sessions/{id}/command", {
        params: {
          path: { id: sessionId },
        },
        body: request as components["schemas"]["SendCommandApiRequest"],
      });

      if (error || !response.ok) {
        throw new Error(readPromptErrorMessage(error, response));
      }

      sendError.value = undefined;
    } catch (caughtError) {
      // Nothing started, so no status event will come to undo the working it was shown as.
      if (before && selectedSession.value?.activityStatus === "busy") {
        Object.assign(selectedSession.value, before);
      }
      const message = caughtError instanceof Error ? caughtError.message : "Failed to send command";
      sendError.value = message;
      console.error("Failed to send command", caughtError);
      // What was typed comes back, unless something new has been typed since.
      if (draft.text.length === 0) setText(typed);
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

    const before = selectedSession.value
      ? {
          activityStatus: selectedSession.value.activityStatus,
          lifecycleStatus: selectedSession.value.lifecycleStatus,
          sessionStatus: selectedSession.value.sessionStatus,
        }
      : undefined;
    if (selectedSession.value) {
      selectedSession.value.activityStatus = "busy";
      selectedSession.value.lifecycleStatus = "running";
      selectedSession.value.sessionStatus = "active";
    }

    const typed = draft.text;
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

    void postCommand(request, typed, before);

    return true;
  }

  return {
    error: readonly(sendError),
    sendCommand,
  };
}
