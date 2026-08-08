import { shallowRef } from "vue";
import type { components } from "@/api/generated/schema";
import { api } from "@/api/client";

/**
 * Composable for answering or rejecting question tool requests.
 * Each call returns fresh `loading` and `error` state.
 */
export function useQuestionAnswer(sessionId: string) {
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
        throw new Error(`Failed to answer question: ${response.statusText}`);
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
        throw new Error(`Failed to reject question: ${response.statusText}`);
      }
    } finally {
      loading.value = false;
    }
  }

  return { loading, error, answerQuestion, rejectQuestion };
}
