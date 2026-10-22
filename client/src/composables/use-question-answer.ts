import { shallowRef } from "vue";
import type { components } from "@/api/generated/schema";
import { useMachineTarget } from "@/lib/machine-target";

const ALREADY_ANSWERED = "This question was already answered.";

/** A question answered from another device is gone by the time this one answers (404, or 409): say so, not "Not Found". */
function failure(response: Response, fallback: string): Error {
  return new Error(response.status === 404 || response.status === 409 ? ALREADY_ANSWERED : `${fallback}: ${response.statusText}`);
}

/**
 * Composable for answering or rejecting question tool requests.
 * Each call returns fresh `loading` and `error` state.
 */
export function useQuestionAnswer(sessionId: string) {
  const { api } = useMachineTarget();
  const loading = shallowRef(false);
  const error = shallowRef<string | null>(null);

  async function answerQuestion(requestId: string, answers: string[][]): Promise<void> {
    loading.value = true;
    error.value = null;
    try {
      const { error: apiError, response } = await api.POST("/api/sessions/{id}/questions/{requestId}/answer", {
        params: {
          path: { id: sessionId, requestId },
        },
        body: { answers } as components["schemas"]["QuestionAnswerApiRequest"],
      });

      if (apiError || !response.ok) {
        throw failure(response, "Failed to answer question");
      }
    } finally {
      loading.value = false;
    }
  }

  async function rejectQuestion(requestId: string): Promise<void> {
    loading.value = true;
    error.value = null;
    try {
      const { error: apiError, response } = await api.POST("/api/sessions/{id}/questions/{requestId}/reject", {
        params: {
          path: { id: sessionId, requestId },
        },
      });

      if (apiError || !response.ok) {
        throw failure(response, "Failed to reject question");
      }
    } finally {
      loading.value = false;
    }
  }

  return { loading, error, answerQuestion, rejectQuestion };
}
