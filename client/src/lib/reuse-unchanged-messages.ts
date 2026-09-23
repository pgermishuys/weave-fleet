import type { AccumulatedMessage } from "@/lib/client-types"

/**
 * A fresh snapshot's messages, with every message that hasn't changed since `kept` swapped for its kept object. The
 * conversation keeps what it derived from those, so taking the fresh snapshot re-renders only what changed.
 */
export function reuseUnchangedMessages(
  kept: readonly AccumulatedMessage[],
  fresh: AccumulatedMessage[],
): AccumulatedMessage[] {
  if (kept.length === 0) {
    return fresh
  }

  const keptById = new Map(kept.map((message) => [message.messageId, message]))
  return fresh.map((message) => {
    const previous = keptById.get(message.messageId)
    return previous && sameData(previous, message) ? previous : message
  })
}

/** Deep equality for plain data (what a snapshot holds): objects, arrays and primitives. */
function sameData(left: unknown, right: unknown): boolean {
  if (left === right) {
    return true
  }

  if (typeof left !== "object" || typeof right !== "object" || left === null || right === null) {
    return false
  }

  if (Array.isArray(left) || Array.isArray(right)) {
    return Array.isArray(left)
      && Array.isArray(right)
      && left.length === right.length
      && left.every((item, index) => sameData(item, right[index]))
  }

  const leftRecord = left as Record<string, unknown>
  const rightRecord = right as Record<string, unknown>
  const keys = Object.keys(leftRecord)
  return keys.length === Object.keys(rightRecord).length
    && keys.every((key) => Object.hasOwn(rightRecord, key) && sameData(leftRecord[key], rightRecord[key]))
}
