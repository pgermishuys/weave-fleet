import { shallowRef } from "vue";
import { apiFetch } from "@/lib/api-client";

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
      const response = await apiFetch(
        `/api/sessions/${encodeURIComponent(sessionId)}/questions/${encodeURIComponent(requestId)}/answer`,
        {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ answers }),
        },
      );
      if (!response.ok) {
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
      const response = await apiFetch(
        `/api/sessions/${encodeURIComponent(sessionId)}/questions/${encodeURIComponent(requestId)}/reject`,
        { method: "POST" },
      );
      if (!response.ok) {
        throw new Error(`Failed to reject question: ${response.statusText}`);
      }
    } finally {
      loading.value = false;
    }
  }

  return { loading, error, answerQuestion, rejectQuestion };
}
