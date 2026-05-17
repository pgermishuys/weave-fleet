import type { AccumulatedToolPart } from "./api-types";

// ── Question tool input schema (mirrors opencode's Question.Info) ─────────────

export interface QuestionOption {
  /** Display text — 1-5 words, concise. */
  label: string;
  /** Explanation of the choice. */
  description: string;
}

export interface QuestionInfo {
  /** Very short label (max 30 chars) — used as the form section header. */
  header: string;
  /** Complete question text. */
  question: string;
  /** Available choices. */
  options: QuestionOption[];
  /** Allow selecting multiple choices. Default: false. */
  multiple?: boolean;
  /** Allow typing a custom answer. Default: true. */
  custom?: boolean;
}

export interface QuestionToolInput {
  questions: QuestionInfo[];
}

// ── Answer request DTO (matches POST /api/sessions/{id}/questions/{requestId}/answer) ──

export interface QuestionAnswerRequest {
  /** Array of selected labels per question. */
  answers: string[][];
}

// ── Helper ───────────────────────────────────────────────────────────────────

/**
 * Returns true if the given tool part was emitted by the question tool.
 * Questions are regular tool parts with `tool === "question"`.
 */
export function isQuestionPart(part: AccumulatedToolPart): boolean {
  return part.tool === "question";
}

/**
 * Extracts the question tool input from a tool part's state.
 * Returns null if the part is not a question or the state is malformed.
 */
export function getQuestionInput(part: AccumulatedToolPart): QuestionToolInput | null {
  if (!isQuestionPart(part)) return null;

  const state = part.state as Record<string, unknown> | null;
  if (!state || typeof state !== "object") return null;

  // OpenCode tool state: { status: "pending"|"running"|"completed"|"error", input?: { questions: [...] } }
  const input = state.input as Record<string, unknown> | undefined;
  if (!input || !Array.isArray(input.questions)) return null;

  return input as unknown as QuestionToolInput;
}

/**
 * Extracts the answers from a completed question tool part's state.
 * Returns null if the part is not completed or has no answers.
 */
export function getQuestionAnswers(part: AccumulatedToolPart): string[][] | null {
  if (!isQuestionPart(part)) return null;

  const state = part.state as Record<string, unknown> | null;
  if (!state || typeof state !== "object") return null;
  if (state.status !== "completed") return null;

  const metadata = state.metadata as Record<string, unknown> | undefined;
  if (!metadata || !Array.isArray(metadata.answers)) return null;

  return metadata.answers as string[][];
}

/**
 * Returns the status of a question tool part.
 */
export function getQuestionStatus(part: AccumulatedToolPart): "pending" | "running" | "completed" | "error" | "unknown" {
  const state = part.state as Record<string, unknown> | null;
  if (!state || typeof state !== "object") return "unknown";

  const status = state.status;
  if (status === "pending" || status === "running" || status === "completed" || status === "error") {
    return status;
  }
  return "unknown";
}
