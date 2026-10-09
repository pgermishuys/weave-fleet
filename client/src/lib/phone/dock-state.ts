/**
 * What sits at the bottom of the phone session: the composer, or what the agent waits on. "The bottom is for whatever
 * needs you": a permission first (oldest first), then a question; anything you put off ("Later") steps aside as a pill
 * above the composer. No Vue.
 */
import type { PermissionAsk } from "@/composables/use-session-permissions";
import type { AccumulatedMessage, AccumulatedToolPart } from "@/lib/client-types";
import { getQuestionInput, isQuestionPart, type QuestionInfo } from "@/lib/question-types";

export interface PendingQuestion {
  requestId: string;
  question: QuestionInfo;
  /** Questions after the first in the same ask. */
  more: number;
}

export type Dock =
  | { kind: "composer"; later: number }
  | { kind: "permission"; ask: PermissionAsk; later: number }
  | { kind: "question"; pending: PendingQuestion; later: number };

/**
 * The question tool call still waiting for an answer, newest first. A call answered anywhere counts as answered: the
 * snapshot and a live update can carry the same call as two parts, one still running.
 */
export function pendingQuestion(messages: readonly AccumulatedMessage[]): PendingQuestion | null {
  const settled = new Set<string>();
  for (const message of messages) {
    for (const part of message.parts) {
      const status = part.type === "tool" ? (part.state as { status?: string } | null)?.status : undefined;
      if (part.type === "tool" && isQuestionPart(part) && (status === "completed" || status === "error")) settled.add(part.callId);
    }
  }

  for (const message of [...messages].reverse()) {
    for (const part of [...message.parts].reverse()) {
      if (part.type !== "tool" || !isQuestionPart(part) || settled.has(part.callId)) continue;
      const status = (part.state as { status?: string } | null)?.status;
      if (status !== "pending" && status !== "running") continue;
      const input = getQuestionInput(part as AccumulatedToolPart);
      if (input?.questions.length) return { requestId: part.callId, question: input.questions[0], more: input.questions.length - 1 };
    }
  }
  return null;
}

export interface DockInput {
  permissions: readonly PermissionAsk[];
  question: PendingQuestion | null;
  /** Asks put off with "Later", by id (a permission's id, a question's request id). */
  later: ReadonlySet<string>;
  /** The ask a notification opened the session for: it goes first. */
  focus?: string | null;
}

export function chooseDock(input: DockInput): Dock {
  const byAge = [...input.permissions].sort((a, b) => a.askedAt.localeCompare(b.askedAt));
  const focused = input.focus ? byAge.find((ask) => ask.id === input.focus) : undefined;
  const ordered = focused ? [focused, ...byAge.filter((ask) => ask !== focused)] : byAge;
  const waitingIds = [...ordered.map((ask) => ask.id), ...(input.question ? [input.question.requestId] : [])];
  const later = waitingIds.filter((id) => input.later.has(id)).length;

  const permission = ordered.find((ask) => !input.later.has(ask.id));
  if (permission) return { kind: "permission", ask: permission, later };
  if (input.question && !input.later.has(input.question.requestId)) return { kind: "question", pending: input.question, later };
  return { kind: "composer", later };
}
