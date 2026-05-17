import { computed } from "vue";
import type { AccumulatedMessage, AccumulatedToolPart } from "@/lib/api-types";
import { isQuestionPart, getQuestionStatus, getQuestionInput } from "@/lib/question-types";

export interface ActiveQuestion {
  /** The raw tool part (contains callId, state etc.) */
  part: AccumulatedToolPart;
  /** Fleet session ID this question belongs to */
  sessionId: string;
  /** The question header from the tool input */
  header: string;
}

/**
 * Derives the list of currently active (pending/running) question tool parts
 * from the provided list of accumulated messages.
 */
export function useActiveQuestions(
  messages: { value: AccumulatedMessage[] },
  sessionId: string,
) {
  const activeQuestions = computed<ActiveQuestion[]>(() => {
    const result: ActiveQuestion[] = [];

    for (const message of messages.value) {
      for (const part of message.parts) {
        if (part.type !== "tool") continue;
        const toolPart = part as AccumulatedToolPart;
        if (!isQuestionPart(toolPart)) continue;

        const status = getQuestionStatus(toolPart);
        if (status !== "pending" && status !== "running") continue;

        const input = getQuestionInput(toolPart);
        const header = input?.questions?.[0]?.header ?? "Question";

        result.push({ part: toolPart, sessionId, header });
      }
    }

    return result;
  });

  const hasActiveQuestions = computed(() => activeQuestions.value.length > 0);

  return { activeQuestions, hasActiveQuestions };
}
