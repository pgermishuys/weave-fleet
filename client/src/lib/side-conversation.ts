import type { AutocompleteCommand } from "@/api/client";
import type { AccumulatedMessage } from "@/lib/client-types";
import { compareMessageIds } from "@/lib/event-state";

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
 * A side conversation's own messages: those after `boundaryMessageId`, the newest one it copied from its session. A
 * boundary that isn't among them is an id that sorts after what was copied (GitHub Copilot names one, since Fleet splits
 * a reply into steps the harness can't count), or one older than what's loaded: the messages that sort after it. All of
 * them when there's no boundary (it copied nothing).
 */
export function messagesAfter<T extends Pick<AccumulatedMessage, "messageId">>(
  messages: readonly T[],
  boundaryMessageId: string | null | undefined,
): readonly T[] {
  if (!boundaryMessageId) {
    return messages;
  }

  const boundary = messages.findIndex((message) => message.messageId === boundaryMessageId);
  if (boundary >= 0) {
    return messages.slice(boundary + 1);
  }
  const after = messages.filter((message) => compareMessageIds(message.messageId, boundaryMessageId) > 0);
  return after.length === messages.length ? messages : after;
}
