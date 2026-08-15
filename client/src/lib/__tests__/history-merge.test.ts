import { describe, expect, it } from "vitest"
import { prependHistoryPage } from "@/lib/history-merge"
import type { AccumulatedMessage } from "@/lib/client-types"
import type { MessageLifecyclePayload } from "@/lib/domain-events"

function createAccumulatedMessage(overrides: {
  messageId: string
  role: "user" | "assistant"
  text?: string
  createdAt?: number
}): AccumulatedMessage {
  return {
    messageId: overrides.messageId,
    sessionId: "session-1",
    role: overrides.role,
    parts: overrides.text
      ? [{ partId: `${overrides.messageId}-text-1`, type: "text", text: overrides.text }]
      : [],
    createdAt: overrides.createdAt,
  }
}

function createLifecyclePayload(overrides: {
  id: string
  role: string
  text?: string
  createdAt?: number
}): MessageLifecyclePayload {
  return {
    info: {
      id: overrides.id,
      role: overrides.role,
      sessionID: "session-1",
      agent: null,
      modelID: null,
      parentID: null,
      time: {
        created: overrides.createdAt ?? 0,
        completed: null,
      },
      cost: null,
      tokens: null,
    },
    parts: overrides.text
      ? [
          {
            id: `${overrides.id}-text-1`,
            sessionID: "session-1",
            messageID: overrides.id,
            type: "text",
            text: overrides.text,
          },
        ]
      : [],
  }
}

describe("prependHistoryPage", () => {
  it("prepends_older_page_before_current_messages", () => {
    const current = [
      createAccumulatedMessage({ messageId: "msg-3", role: "user", text: "third", createdAt: 3000 }),
      createAccumulatedMessage({ messageId: "msg-4", role: "assistant", text: "fourth", createdAt: 4000 }),
    ]

    const page = [
      createLifecyclePayload({ id: "msg-1", role: "user", text: "first", createdAt: 1000 }),
      createLifecyclePayload({ id: "msg-2", role: "assistant", text: "second", createdAt: 2000 }),
    ]

    const result = prependHistoryPage(current, page)

    expect(result).toHaveLength(4)
    expect(result.map((m) => m.messageId)).toEqual(["msg-1", "msg-2", "msg-3", "msg-4"])
  })

  it("preserves_page_message_order_exactly", () => {
    const current: AccumulatedMessage[] = []

    // Page messages in specific order (not sorted by timestamp)
    const page = [
      createLifecyclePayload({ id: "msg-2", role: "assistant", text: "second", createdAt: 2000 }),
      createLifecyclePayload({ id: "msg-1", role: "user", text: "first", createdAt: 1000 }),
      createLifecyclePayload({ id: "msg-3", role: "user", text: "third", createdAt: 3000 }),
    ]

    const result = prependHistoryPage(current, page)

    expect(result).toHaveLength(3)
    // Preserve exact page order, not sorted by timestamp
    expect(result.map((m) => m.messageId)).toEqual(["msg-2", "msg-1", "msg-3"])
  })

  it("deduplicates_by_id_keeping_current_messages", () => {
    // Current state has live message with streaming parts
    const current = [
      createAccumulatedMessage({
        messageId: "msg-2",
        role: "assistant",
        text: "live streamed text with more content",
        createdAt: 2000,
      }),
      createAccumulatedMessage({ messageId: "msg-3", role: "user", text: "third", createdAt: 3000 }),
    ]

    // Page includes msg-2 with less content (snapshot before streaming completed)
    const page = [
      createLifecyclePayload({ id: "msg-1", role: "user", text: "first", createdAt: 1000 }),
      createLifecyclePayload({ id: "msg-2", role: "assistant", text: "partial", createdAt: 2000 }),
    ]

    const result = prependHistoryPage(current, page)

    expect(result).toHaveLength(3)
    expect(result.map((m) => m.messageId)).toEqual(["msg-1", "msg-2", "msg-3"])
    
    // Current/live message is authoritative (has more content)
    const msg2 = result.find((m) => m.messageId === "msg-2")
    expect(msg2?.parts[0]).toMatchObject({
      type: "text",
      text: "live streamed text with more content",
    })
  })

  it("does_not_reposition_existing_messages", () => {
    // Current state has messages in arrival order
    const current = [
      createAccumulatedMessage({ messageId: "user-1", role: "user", text: "first", createdAt: 1000 }),
      createAccumulatedMessage({ messageId: "assistant-1", role: "assistant", text: "reply" }), // no timestamp
      createAccumulatedMessage({ messageId: "user-2", role: "user", text: "second", createdAt: 2000 }),
    ]

    // Page has older messages
    const page = [
      createLifecyclePayload({ id: "msg-0", role: "user", text: "oldest", createdAt: 500 }),
    ]

    const result = prependHistoryPage(current, page)

    expect(result).toHaveLength(4)
    expect(result.map((m) => m.messageId)).toEqual(["msg-0", "user-1", "assistant-1", "user-2"])
    
    // Existing messages maintain their relative order
    expect(result[1]?.messageId).toBe("user-1")
    expect(result[2]?.messageId).toBe("assistant-1")
    expect(result[3]?.messageId).toBe("user-2")
  })

  it("handles_empty_page", () => {
    const current = [
      createAccumulatedMessage({ messageId: "msg-1", role: "user", text: "first", createdAt: 1000 }),
    ]

    const page: MessageLifecyclePayload[] = []

    const result = prependHistoryPage(current, page)

    expect(result).toHaveLength(1)
    expect(result).toEqual(current)
  })

  it("handles_empty_current_state", () => {
    const current: AccumulatedMessage[] = []

    const page = [
      createLifecyclePayload({ id: "msg-1", role: "user", text: "first", createdAt: 1000 }),
      createLifecyclePayload({ id: "msg-2", role: "assistant", text: "second", createdAt: 2000 }),
    ]

    const result = prependHistoryPage(current, page)

    expect(result).toHaveLength(2)
    expect(result.map((m) => m.messageId)).toEqual(["msg-1", "msg-2"])
  })

  it("handles_complete_overlap", () => {
    const current = [
      createAccumulatedMessage({ messageId: "msg-1", role: "user", text: "current version", createdAt: 1000 }),
      createAccumulatedMessage({ messageId: "msg-2", role: "assistant", text: "current version", createdAt: 2000 }),
    ]

    // Page has same messages (e.g., reconnect scenario)
    const page = [
      createLifecyclePayload({ id: "msg-1", role: "user", text: "page version", createdAt: 1000 }),
      createLifecyclePayload({ id: "msg-2", role: "assistant", text: "page version", createdAt: 2000 }),
    ]

    const result = prependHistoryPage(current, page)

    expect(result).toHaveLength(2)
    expect(result.map((m) => m.messageId)).toEqual(["msg-1", "msg-2"])
    
    // Current messages are kept (authoritative)
    expect(result[0]?.parts[0]).toMatchObject({ type: "text", text: "current version" })
    expect(result[1]?.parts[0]).toMatchObject({ type: "text", text: "current version" })
  })
})
