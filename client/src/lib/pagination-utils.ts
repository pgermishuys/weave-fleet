/**
 * Pure pagination utility functions for server-side message slicing
 * and client-side batch merging.
 *
 * Extracted for testability — no React or Next.js dependencies.
 */

import type { AccumulatedMessage, AccumulatedPart } from "@/lib/api-types";

// ─── Types ──────────────────────────────────────────────────────────────────

/** Raw SDK message shape as returned by the API. */
export interface SDKMessageInfo {
  id: string;
  sessionID: string;
  role: string;
  time?: { created?: number; completed?: number };
  cost?: number;
  tokens?: { input: number; output: number; reasoning: number };
  agent?: string;
  modelID?: string;
  parentID?: string;
}

export interface SDKMessagePart {
  id: string;
  messageID: string;
  sessionID: string;
  type: string;
  text?: string;
  tool?: string;
  callID?: string;
  state?: unknown;
  cost?: number;
  tokens?: { input: number; output: number; reasoning: number };
  // File part fields
  mime?: string;
  filename?: string;
  url?: string;
}

export interface SDKMessage {
  info: SDKMessageInfo;
  parts: SDKMessagePart[];
}

/** Options for slicing a message array. */
export interface SliceOptions {
  limit: number;
  /** Message ID cursor — return messages older than this ID. */
  before?: string;
}

/** Result of slicing a message array. */
export interface SliceResult<T> {
  messages: T[];
  pagination: {
    hasMore: boolean;
    oldestMessageId: string | null;
    totalCount: number;
  };
}

// ─── sliceMessages ──────────────────────────────────────────────────────────

/**
 * Given a full (sorted ascending by time) message array, return a paginated
 * slice plus metadata.
 *
 * - No `before` → returns the last `limit` messages (tail).
 * - With `before` → finds the message with that ID and returns the `limit`
 *   messages immediately preceding it.
 * - If `before` ID is not found, falls back to returning the tail.
 */
export function sliceMessages<T extends { info: { id: string } }>(
  allMessages: T[],
  { limit, before }: SliceOptions,
): SliceResult<T> {
  const totalCount = allMessages.length;

  if (totalCount === 0) {
    return {
      messages: [],
      pagination: { hasMore: false, oldestMessageId: null, totalCount: 0 },
    };
  }

  let endIndex: number;

  if (before) {
    const cursorIndex = allMessages.findIndex((m) => m.info.id === before);
    // If cursor not found, fall back to tail behavior
    endIndex = cursorIndex === -1 ? totalCount : cursorIndex;
  } else {
    endIndex = totalCount;
  }

  const startIndex = Math.max(0, endIndex - limit);
  const slice = allMessages.slice(startIndex, endIndex);
  const hasMore = startIndex > 0;
  const oldestMessageId = slice.length > 0 ? slice[0].info.id : null;

  return {
    messages: slice,
    pagination: { hasMore, oldestMessageId, totalCount },
  };
}

// ─── prependMessages ────────────────────────────────────────────────────────

/**
 * Merge older messages before the existing array, deduplicating by messageId.
 *
 * Handles the case where SSE has already added a message that also appears in
 * the older batch.
 */
export function prependMessages(
  existing: AccumulatedMessage[],
  older: AccumulatedMessage[],
): AccumulatedMessage[] {
  if (older.length === 0) return existing;

  const existingIds = new Set(existing.map((m) => m.messageId));
  const uniqueOlder = older.filter((m) => !existingIds.has(m.messageId));

  if (uniqueOlder.length === 0) return existing;

  return [...uniqueOlder, ...existing];
}

// ─── convertSDKMessageToAccumulated ─────────────────────────────────────────

/**
 * Convert a raw SDK message (as returned by the API) into an AccumulatedMessage.
 *
 * This is the same conversion logic that was inline in
 * `useSessionEvents.loadMessages()` (lines 79-116), extracted for reuse by
 * the pagination hook.
 */
export function convertSDKMessageToAccumulated(msg: SDKMessage): AccumulatedMessage {
  const parts: AccumulatedPart[] = [];
  let cost = 0;
  let tokensInput = 0;
  let tokensOutput = 0;
  let tokensReasoning = 0;

  for (const part of msg.parts) {
    if (part.type === "text") {
      parts.push({ partId: part.id, type: "text", text: part.text ?? "" });
    } else if (part.type === "tool") {
      parts.push({
        partId: part.id,
        type: "tool",
        tool: part.tool ?? "",
        callId: part.callID ?? "",
        state: part.state,
      });
    } else if (part.type === "file") {
      parts.push({
        partId: part.id,
        type: "file",
        mime: part.mime ?? "",
        filename: part.filename,
        url: part.url ?? "",
      });
    } else if (part.type === "step-finish") {
      cost += part.cost ?? 0;
      tokensInput += part.tokens?.input ?? 0;
      tokensOutput += part.tokens?.output ?? 0;
      tokensReasoning += part.tokens?.reasoning ?? 0;
    }
  }

  return {
    messageId: msg.info.id,
    sessionId: msg.info.sessionID,
    role: msg.info.role === "user" ? ("user" as const) : ("assistant" as const),
    parts,
    createdAt: msg.info.time?.created,
    completedAt: msg.info.time?.completed,
    agent: msg.info.agent,
    modelID: msg.info.modelID,
    parentID: msg.info.parentID,
    cost: cost || (msg.info.cost ?? 0),
    tokens:
      tokensInput || tokensOutput || tokensReasoning
        ? { input: tokensInput, output: tokensOutput, reasoning: tokensReasoning }
        : msg.info.tokens
          ? {
              input: msg.info.tokens.input,
              output: msg.info.tokens.output,
              reasoning: msg.info.tokens.reasoning,
            }
          : undefined,
  };
}
