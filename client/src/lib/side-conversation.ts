import type { AutocompleteCommand } from "@/api/client";
import type { AccumulatedMessage } from "@/lib/client-types";

/**
 * Side conversations: `/btw <question>` asks in a fork of the session made at its last finished turn, so the session
 * carries on undisturbed. The answer shows in a panel above the composer; while it's open, the composer talks to it.
 */

/** The command, as the `/` popup lists it. Fleet's own: it never reaches the harness. */
export const SIDE_QUESTION_COMMAND: AutocompleteCommand = {
  name: "btw",
  description: "Ask a side question without disturbing the session",
};

const SIDE_QUESTION_PATTERN = /^\s*\/btw(?:\s+([\s\S]*))?$/;

/**
 * The question in a `/btw` draft: the text after `/btw`, trimmed ("" for a bare `/btw`). Null when the draft isn't a
 * `/btw` command (`/btwx` isn't one).
 */
export function parseSideQuestion(draft: string): string | null {
  const match = SIDE_QUESTION_PATTERN.exec(draft);
  return match ? (match[1] ?? "").trim() : null;
}

/**
 * A side conversation's own messages: those after `boundaryMessageId`, the newest one it copied from its session. All
 * of them when there's no boundary (it copied nothing), or when the boundary isn't among them (only newer messages are
 * loaded).
 */
export function messagesAfter<T extends Pick<AccumulatedMessage, "messageId">>(
  messages: readonly T[],
  boundaryMessageId: string | null | undefined,
): readonly T[] {
  if (!boundaryMessageId) {
    return messages;
  }

  const boundary = messages.findIndex((message) => message.messageId === boundaryMessageId);
  return boundary < 0 ? messages : messages.slice(boundary + 1);
}
