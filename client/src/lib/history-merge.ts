/**
 * Pure helpers for merging paginated history with live state.
 */

import type {
  AccumulatedFilePart,
  AccumulatedMessage,
  AccumulatedPart,
  AccumulatedReasoningPart,
  AccumulatedTextPart,
  AccumulatedToolPart,
} from "@/lib/client-types"
import type { MessageLifecyclePayload, MessageEventPart } from "@/lib/domain-events"

/**
 * Merge an older history page before existing messages.
 * 
 * - Preserves page message order exactly
 * - Places older page before current messages
 * - Deduplicates by message ID: existing/live messages are authoritative
 *   (they have fresher parts/metadata from live events)
 * - Does not reposition existing messages
 */
export function prependHistoryPage(
  currentMessages: AccumulatedMessage[],
  pageMessages: MessageLifecyclePayload[],
): AccumulatedMessage[] {
  const existingIds = new Set(currentMessages.map((message) => message.messageId))

  const olderMessages: AccumulatedMessage[] = []
  for (const message of pageMessages) {
    if (existingIds.has(message.info.id)) {
      continue
    }

    olderMessages.push(convertToAccumulatedMessage(message))
  }

  return [...olderMessages, ...currentMessages]
}

function convertToAccumulatedMessage(message: MessageLifecyclePayload): AccumulatedMessage {
  const role: "user" | "assistant" = message.info.role === "user" ? "user" : "assistant"
  const modelID = typeof message.info.modelID === "string" ? message.info.modelID : undefined
  const parts = message.parts
    .map(convertPart)
    .filter((part): part is AccumulatedPart => part !== null)

  return {
    messageId: message.info.id,
    sessionId: message.info.sessionID,
    role,
    parts,
    createdAt: message.info.time.created,
    completedAt: message.info.time.completed ?? undefined,
    agent: message.info.agent ?? undefined,
    modelID,
    parentID: message.info.parentID ?? undefined,
    cost: message.info.cost ?? undefined,
    tokens: message.info.tokens ?? undefined,
  }
}

function convertPart(part: MessageEventPart): AccumulatedPart | null {
  if (part.type === "text") {
    return {
      partId: part.id,
      type: "text",
      text: part.text ?? "",
    } satisfies AccumulatedTextPart
  }

  if (part.type === "reasoning") {
    return {
      partId: part.id,
      type: "reasoning",
      text: part.text ?? "",
      summary: part.summary ?? undefined,
    } satisfies AccumulatedReasoningPart
  }

  if (part.type === "tool") {
    return {
      partId: part.id,
      type: "tool",
      tool: part.tool ?? "",
      callId: part.callID ?? "",
      state: part.state,
    } satisfies AccumulatedToolPart
  }

  if (part.type === "file") {
    return {
      partId: part.id,
      type: "file",
      mime: part.mime ?? "",
      filename: part.filename ?? undefined,
      url: part.url ?? "",
    } satisfies AccumulatedFilePart
  }
  
  return null
}
