/**
 * Pure state update helpers for accumulating SSE events into renderable messages.
 * Extracted from useSessionEvents for testability — no React dependencies.
 */

import type {
  AccumulatedMessage,
  AccumulatedReasoningPart,
  AccumulatedTextPart,
  AccumulatedToolPart,
  AccumulatedFilePart,
} from "@/lib/api-types";
import { sortAccumulatedMessagesChronologically } from "@/lib/pagination-utils";

function sortMessagesChronologically(messages: AccumulatedMessage[]): AccumulatedMessage[] {
  return sortAccumulatedMessagesChronologically(messages);
}

 
export function ensureMessage(
  prev: AccumulatedMessage[],
  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  info: Record<string, any>
): AccumulatedMessage[] {
  const messageId: string = info.id;
  const existing = prev.find((m) => m.messageId === messageId);
  if (existing) return prev;

  const role: "user" | "assistant" =
    info.role === "user" ? "user" : "assistant";
  const modelID = typeof info.modelID === "string"
    ? info.modelID
    : undefined;
  const newMsg: AccumulatedMessage = {
    messageId,
    sessionId: info.sessionID ?? "",
    role,
    parts: [],
    createdAt: info.time?.created,
    // v2: both UserMessage and AssistantMessage have agent: string
    agent: info.agent,
    modelID,
    parentID: info.parentID,
  };
  return sortMessagesChronologically([...prev, newMsg]);
}

/**
 * Merges completion data into an existing message.
 * Isolated from ensureMessage() for easy revert if needed.
 * Only updates fields that were previously unset (null-safe merge).
 */
export function mergeMessageUpdate(
  prev: AccumulatedMessage[],
   
  info: { id: string; time?: { created?: number; completed?: number }; cost?: number; tokens?: { input: number; output: number; reasoning: number }; parts?: Array<Record<string, unknown>>; [key: string]: unknown }
): AccumulatedMessage[] {
  const index = prev.findIndex((m) => m.messageId === info.id);
  if (index === -1) return prev; // message not found, no-op
  const existing = prev[index];
  const createdAt = info.time?.created;
  const completedAt = info.time?.completed;
  const modelID = typeof info.modelID === "string"
    ? info.modelID
    : undefined;

  // Merge tokens and cost from the message-level info.
  // step-finish parts accumulate these during streaming, but if any were
  // missed (e.g. reconnect gap) the final message.updated carries the totals.
  // Use the larger value between what we accumulated and what the server reports,
  // since the server total is authoritative for completed messages.
  const infoTokens = info.tokens;
  const infoCost = info.cost ?? 0;
  const mergedTokens = infoTokens
    ? {
        input: Math.max(existing.tokens?.input ?? 0, infoTokens.input ?? 0),
        output: Math.max(existing.tokens?.output ?? 0, infoTokens.output ?? 0),
        reasoning: Math.max(existing.tokens?.reasoning ?? 0, infoTokens.reasoning ?? 0),
      }
    : existing.tokens;
  const mergedCost = Math.max(existing.cost ?? 0, infoCost);

  const hasNewCompletedAt = completedAt && !existing.completedAt;
  const hasNewCreatedAt = typeof createdAt === "number" && existing.createdAt !== createdAt;
  const hasNewTokens = mergedTokens && !existing.tokens;
  const hasUpdatedTokens =
    mergedTokens &&
    existing.tokens &&
    (mergedTokens.input !== existing.tokens.input ||
      mergedTokens.output !== existing.tokens.output ||
      mergedTokens.reasoning !== existing.tokens.reasoning);
  const hasNewCost = mergedCost > (existing.cost ?? 0);
  const snapshotParts = Array.isArray(info.parts)
    ? info.parts
        .map((part, partIndex) => mapCommittedSnapshotPart(info.id, part, partIndex))
        .filter((part): part is NonNullable<typeof part> => part != null)
    : null;
  const mergedSnapshotParts = snapshotParts ? mergeCommittedSnapshotParts(existing.parts, snapshotParts) : null;
  const hasSnapshotParts = mergedSnapshotParts != null;
  const hasNewModelID = Boolean(modelID && modelID !== existing.modelID);

  if (!hasNewCompletedAt && !hasNewCreatedAt && !hasNewTokens && !hasUpdatedTokens && !hasNewCost && !hasSnapshotParts && !hasNewModelID) {
    return prev; // nothing new to merge
  }

  const updated = prev.slice();
  updated[index] = {
    ...existing,
    ...(hasSnapshotParts ? { parts: mergedSnapshotParts } : {}),
    ...(hasNewCreatedAt ? { createdAt } : {}),
    ...(hasNewCompletedAt ? { completedAt } : {}),
    ...(hasNewModelID ? { modelID } : {}),
    tokens: mergedTokens,
    cost: mergedCost,
  };
  return sortMessagesChronologically(updated);
}

function mapCommittedSnapshotPart(
  messageId: string,
  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  part: Record<string, any>,
  index: number,
): AccumulatedTextPart | AccumulatedFilePart | null {
  if (part.type === "text") {
    return {
      partId: typeof part.id === "string" ? part.id : `${messageId}-text-${index}`,
      type: "text",
      text: typeof part.text === "string" ? part.text : "",
    };
  }

  if (part.type === "file") {
    return {
      partId: typeof part.id === "string" ? part.id : `${messageId}-file-${index}`,
      type: "file",
      mime: typeof part.mime === "string" ? part.mime : "",
      filename: typeof part.filename === "string" ? part.filename : undefined,
      url: typeof part.url === "string" ? part.url : "",
    };
  }

  return null;
}

function mergeCommittedSnapshotParts(
  existingParts: AccumulatedMessage["parts"],
  snapshotParts: Array<AccumulatedTextPart | AccumulatedFilePart>,
): AccumulatedMessage["parts"] {
  const snapshotTextParts = snapshotParts.filter((p): p is AccumulatedTextPart => p.type === "text");
  const snapshotFileParts = snapshotParts.filter((p): p is AccumulatedFilePart => p.type === "file");

  const existingTextByPartId = new Map(
    existingParts
      .filter((p): p is AccumulatedTextPart => p.type === "text")
      .map((p) => [p.partId, p]),
  );

  const mergedText = snapshotTextParts.map((snap) => {
    const existing = existingTextByPartId.get(snap.partId);
    if (existing && existing.text.length > snap.text.length) {
      return existing;
    }
    return snap;
  });

  // Merge file parts: snapshot wins for matching IDs, preserve existing ones not in snapshot
  const snapshotFilePartIds = new Set(snapshotFileParts.map((p) => p.partId));
  const existingFileParts = existingParts.filter(
    (p): p is AccumulatedFilePart => p.type === "file" && !snapshotFilePartIds.has(p.partId),
  );
  const mergedFiles = [...snapshotFileParts, ...existingFileParts];

  const otherParts = existingParts.filter(
    (part) => part.type !== "text" && part.type !== "reasoning" && part.type !== "file",
  );

  return [...mergedText, ...mergedFiles, ...otherParts];
}

export function applyPartUpdate(
  prev: AccumulatedMessage[],
  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  part: Record<string, any>
): AccumulatedMessage[] {
  const messageId: string = part.messageID;
  const sessionId: string = part.sessionID;

  // Ensure the message exists
  let msgs = prev;
  if (!msgs.find((m) => m.messageId === messageId)) {
    const newMsg: AccumulatedMessage = {
      messageId,
      sessionId,
      role: "assistant",
      parts: [],
    };
    msgs = [...prev, newMsg];
  }

  return msgs.map((msg) => {
    if (msg.messageId !== messageId) return msg;

    if (part.type === "text") {
      const existingPart = msg.parts.find(
        (p): p is AccumulatedTextPart =>
          (p.type === "text" || p.type === "reasoning") && p.partId === part.id
      );
      if (existingPart) {
        return {
          ...msg,
          parts: msg.parts.map((p) =>
            p.partId === part.id ? { partId: part.id, type: "text", text: part.text ?? "" } : p
          ),
        };
      }
      const newPart: AccumulatedTextPart = {
        partId: part.id,
        type: "text",
        text: part.text ?? "",
      };
      return { ...msg, parts: [...msg.parts, newPart] };
    }

    if (part.type === "reasoning") {
      const existingPart = msg.parts.find(
        (p): p is AccumulatedTextPart | AccumulatedReasoningPart =>
          (p.type === "text" || p.type === "reasoning") && p.partId === part.id
      );
      const newPart: AccumulatedReasoningPart = {
        partId: part.id,
        type: "reasoning",
        text: part.text ?? "",
        summary: part.summary,
      };

      if (existingPart) {
        return {
          ...msg,
          parts: msg.parts.map((p) => (p.partId === part.id ? newPart : p)),
        };
      }

      return { ...msg, parts: [...msg.parts, newPart] };
    }

    if (part.type === "tool") {
      const existingPart = msg.parts.find(
        (p): p is AccumulatedToolPart => p.type === "tool" && p.partId === part.id,
      );
      const existingState = (existingPart?.state ?? {}) as Record<string, unknown>;
      const incomingState = (part.state ?? {}) as Record<string, unknown>;
      const newPart: AccumulatedToolPart = {
        partId: part.id,
        type: "tool",
        tool: part.tool ?? "",
        callId: part.callID ?? "",
        state: existingPart
          ? { ...existingState, ...incomingState }
          : part.state,
      };
      if (existingPart) {
        return {
          ...msg,
          parts: msg.parts.map((p) => (p.partId === part.id ? newPart : p)),
        };
      }
      return { ...msg, parts: [...msg.parts, newPart] };
    }

    if (part.type === "file") {
      const newPart: AccumulatedFilePart = {
        partId: part.id,
        type: "file",
        mime: part.mime ?? "",
        filename: part.filename,
        url: part.url ?? "",
      };
      const existing = msg.parts.find((p) => p.partId === part.id);
      if (existing) {
        return {
          ...msg,
          parts: msg.parts.map((p) => (p.partId === part.id ? newPart : p)),
        };
      }
      return { ...msg, parts: [...msg.parts, newPart] };
    }

    if (part.type === "step-finish") {
      return {
        ...msg,
        cost: (msg.cost ?? 0) + (part.cost ?? 0),
        tokens: {
          input: (msg.tokens?.input ?? 0) + (part.tokens?.input ?? 0),
          output: (msg.tokens?.output ?? 0) + (part.tokens?.output ?? 0),
          reasoning:
            (msg.tokens?.reasoning ?? 0) + (part.tokens?.reasoning ?? 0),
        },
      };
    }

    return msg;
  });
}

export function applyTextDelta(
  prev: AccumulatedMessage[],
  messageId: string,
  partId: string,
  sessionId: string,
  delta: string
): AccumulatedMessage[] {
  const msgIndex = prev.findIndex((m) => m.messageId === messageId);

  if (msgIndex === -1) {
    // Message doesn't exist — append new message with the delta as first part
    const newMsg: AccumulatedMessage = {
      messageId,
      sessionId,
      role: "assistant",
      parts: [{ partId, type: "text", text: delta }],
    };
    return [...prev, newMsg];
  }

  const msg = prev[msgIndex];
  const partIndex = msg.parts.findIndex(
    (p) => (p.type === "text" || p.type === "reasoning") && p.partId === partId
  );

  let updatedMsg: AccumulatedMessage;
  if (partIndex !== -1) {
    const existingPart = msg.parts[partIndex] as AccumulatedTextPart | AccumulatedReasoningPart;
    const newParts = msg.parts.slice();
    newParts[partIndex] = { ...existingPart, text: existingPart.text + delta };
    updatedMsg = { ...msg, parts: newParts };
  } else {
    const newPart: AccumulatedTextPart = { partId, type: "text", text: delta };
    updatedMsg = { ...msg, parts: [...msg.parts, newPart] };
  }

  const next = prev.slice();
  next[msgIndex] = updatedMsg;
  return next;
}

/**
 * Determines if an SSE event is relevant to a specific session.
 * Used by the SSE proxy to filter events before forwarding to the client.
 */
export function isRelevantToSession(
  type: string,
  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  properties: Record<string, any>,
  sessionId: string
): boolean {
  // System events: always forward
  if (type === "server.connected" || type === "server.heartbeat") return true;

  // message.part.updated / message.part.removed — check part.sessionID
  if (type === "message.part.updated" || type === "message.part.removed") {
    return (
      properties?.part?.sessionID === sessionId ||
      properties?.sessionID === sessionId
    );
  }

  // message.updated / message.removed — check info.sessionID
  if (type === "message.updated" || type === "message.removed") {
    return properties?.info?.sessionID === sessionId;
  }

  // session.* events — check properties.sessionID or properties.info.id
  if (type.startsWith("session.")) {
    return (
      properties?.sessionID === sessionId ||
      properties?.info?.id === sessionId
    );
  }

  // permission events
  if (type.startsWith("permission.")) {
    return properties?.sessionID === sessionId;
  }

  // message.part.delta (undocumented event for streaming text)
  if (type === "message.part.delta") {
    return properties?.sessionID === sessionId;
  }

  if (type === "delegation.created" || type === "delegation.updated") {
    return properties?.parentSessionId === sessionId;
  }

  // question.* events emitted by OpenCode when question tool is used
  if (type.startsWith("question.")) {
    return properties?.sessionID === sessionId;
  }

  // Unknown events — skip to avoid noise
  return false;
}
