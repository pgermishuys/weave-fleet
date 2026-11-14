import { describe, expect, it } from "vitest"
import { applyDomainEvent, createSessionStreamState, type SessionStreamState } from "@/lib/domain-event-reducer"
import type { DomainEvent, MessageLifecyclePayload } from "@/lib/domain-events"
import type { SessionSnapshot } from "@/lib/session-snapshot"

function createSnapshot(overrides: Partial<SessionSnapshot> = {}): SessionSnapshot {
  return {
    session: {
      id: "session-1",
      title: "Session 1",
      status: "running",
    },
    messages: [],
    delegations: [],
    activityStatus: "idle",
    lastEventId: 1,
    hasMore: false,
    cursor: null,
    isPartial: false,
    ...overrides,
  }
}

function createMessageLifecyclePayload(
  overrides: {
    id: string
    role: string
    createdAt?: number
    completedAt?: number | null
    text?: string
    partId?: string
    sessionID?: string
    agent?: string | null
    modelID?: string | null
    parentID?: string | null
    cost?: number | null
    tokens?: { input: number; output: number; reasoning: number } | null
  },
): MessageLifecyclePayload {
  const partId = overrides.partId ?? `${overrides.id}-text-1`
  const text = overrides.text

  return {
    info: {
      id: overrides.id,
      role: overrides.role,
      sessionID: overrides.sessionID ?? "session-1",
      agent: overrides.agent ?? null,
      modelID: overrides.modelID ?? null,
      parentID: overrides.parentID ?? null,
      time: {
        created: overrides.createdAt ?? 0,
        completed: overrides.completedAt ?? null,
      },
      cost: overrides.cost ?? null,
      tokens: overrides.tokens ?? null,
    },
    parts: text == null
      ? []
      : [{
          id: partId,
          sessionID: overrides.sessionID ?? "session-1",
          messageID: overrides.id,
          type: "text",
          text,
        }],
  }
}

function createState(overrides: Partial<SessionStreamState> = {}): SessionStreamState {
  return {
    messages: [],
    delegations: [],
    runningWork: [],
    context: null,
    explicitStatus: "idle",
    sessionStatus: "idle",
    lastEventId: null,
    ...overrides,
  }
}

function applyEvents(state: SessionStreamState, events: DomainEvent[]): SessionStreamState {
  return events.reduce((currentState, event) => applyDomainEvent(currentState, event), state)
}

describe("message ordering", () => {
  it("sorts_messages_by_message_id", () => {
    // Messages are sorted by message ID (lexicographic), not arrival order.
    const userPrompt1 = createMessageLifecyclePayload({
      id: "msg_0000010000000001_user1",
      role: "user",
      createdAt: 1000,
      text: "hey",
    })

    const assistantReply1 = createMessageLifecyclePayload({
      id: "msg_0000010000000002_assistant1",
      role: "assistant",
      createdAt: 1500,
      text: "assistant reply",
    })

    const userPrompt2 = createMessageLifecyclePayload({
      id: "msg_0000010000000003_user2",
      role: "user",
      createdAt: 2000,
      text: "yo",
    })

    const state = applyEvents(createState(), [
      {
        type: "message.created",
        payload: userPrompt1,
      },
      {
        type: "message.created",
        payload: assistantReply1,
      },
      {
        type: "message.created",
        payload: userPrompt2,
      },
    ])

    expect(state.messages).toHaveLength(3)
    // Messages appear in chronological order by message ID
    expect(state.messages.map((m) => m.messageId)).toEqual([
      "msg_0000010000000001_user1",
      "msg_0000010000000002_assistant1",
      "msg_0000010000000003_user2",
    ])
    expect(state.messages.map((m) => m.role)).toEqual(["user", "assistant", "user"])
  })

  it("keeps_an_assistant_message_created_by_a_delta_where_it_arrived", () => {
    const userPrompt1 = createMessageLifecyclePayload({
      id: "msg_0000010000000001_user1",
      role: "user",
      createdAt: 1000,
      text: "first",
    })

    const userPrompt2 = createMessageLifecyclePayload({
      id: "msg_0000010000000002_user2",
      role: "user",
      createdAt: 2000,
      text: "second",
    })

    const state = applyEvents(createState(), [
      {
        type: "message.created",
        payload: userPrompt1,
      },
      {
        type: "message.part.delta.streamed",
        payload: {
          sessionID: "session-1",
          messageID: "assistant-1", // No msg_ prefix
          partID: "assistant-text-1",
          field: "text",
          delta: "assistant reply",
        },
      },
      {
        type: "message.created",
        payload: userPrompt2,
      },
    ])

    expect(state.messages).toHaveLength(3)
    // A message whose id isn't a msg_ one, with no time to go by, stays where it arrived.
    expect(state.messages.map((m) => m.messageId)).toEqual([
      "msg_0000010000000001_user1",
      "assistant-1",
      "msg_0000010000000002_user2",
    ])
    expect(state.messages.map((m) => m.role)).toEqual(["user", "assistant", "user"])
  })

  it("sorts_snapshot_messages_by_message_id", () => {
    // Snapshot messages are sorted by message ID (lexicographic).
    const message1 = createMessageLifecyclePayload({
      id: "msg_0000010000000002_msg1",
      role: "user",
      createdAt: 2000,
      text: "second by timestamp",
    })

    const message2 = createMessageLifecyclePayload({
      id: "msg_0000010000000001_msg2",
      role: "assistant",
      createdAt: 1000,
      text: "first by timestamp",
    })

    const message3 = createMessageLifecyclePayload({
      id: "msg_0000010000000003_msg3",
      role: "user",
      createdAt: 1500,
      text: "middle by timestamp",
    })

    // Server provides snapshot in arbitrary order
    const state = createSessionStreamState(createSnapshot({
      messages: [message1, message2, message3],
    }))

    expect(state.messages).toHaveLength(3)
    // Client sorts by message ID
    expect(state.messages.map((m) => m.messageId)).toEqual([
      "msg_0000010000000001_msg2",
      "msg_0000010000000002_msg1",
      "msg_0000010000000003_msg3",
    ])
  })

  it("reorders_when_lifecycle_backfills_createdAt", () => {
    // When a lifecycle event backfills createdAt, the message position is NOT changed
    // because sorting is by message ID, not createdAt.
    const userPrompt = createMessageLifecyclePayload({
      id: "msg_0000010000000001_user1",
      role: "user",
      createdAt: 1000,
      text: "prompt",
    })

    const assistantCreated = createMessageLifecyclePayload({
      id: "msg_0000010000000002_assistant1",
      role: "assistant",
      createdAt: 1500,
      text: "reply",
    })

    const userPrompt2 = createMessageLifecyclePayload({
      id: "msg_0000010000000003_user2",
      role: "user",
      createdAt: 2000,
      text: "second prompt",
    })

    // Simulate: user prompt, assistant delta (no timestamp), second user prompt,
    // then assistant lifecycle event backfills timestamp
    const state = applyEvents(createState(), [
      {
        type: "message.created",
        payload: userPrompt,
      },
      {
        type: "message.part.delta.streamed",
        payload: {
          sessionID: "session-1",
          messageID: "msg_0000010000000002_assistant1",
          partID: "assistant-text-1",
          field: "text",
          delta: "reply",
        },
      },
      {
        type: "message.created",
        payload: userPrompt2,
      },
      {
        type: "message.created",
        payload: assistantCreated,
      },
    ])

    expect(state.messages).toHaveLength(3)
    // Messages are sorted by message ID
    expect(state.messages.map((m) => m.messageId)).toEqual([
      "msg_0000010000000001_user1",
      "msg_0000010000000002_assistant1",
      "msg_0000010000000003_user2",
    ])
    expect(state.messages.map((m) => m.role)).toEqual(["user", "assistant", "user"])
    expect(state.messages[1]?.createdAt).toBe(1500)
  })

  describe("a Pi session: Pi's own ids for its messages, Fleet's msg_ id for a prompt sent later", () => {
    // The shape a Pi session's snapshot has (14 November 2026): the prompt the session was created with and every reply
    // carry Pi's ids, pi-<session>-<role>-<time>; the second prompt carries the msg_ id Fleet gave it.
    const piId = (role: string, at: number) => `pi-5f3c-${role}-${at}`
    const conversation = [
      createMessageLifecyclePayload({ id: piId("user", 1794681358517), role: "user", createdAt: 1794681358517, text: "Write the export plan." }),
      createMessageLifecyclePayload({ id: piId("assistant", 1794681358669), role: "assistant", createdAt: 1794681358669 }),
      createMessageLifecyclePayload({ id: piId("assistant", 1794681358754), role: "assistant", createdAt: 1794681358754, text: "Wrote the plan." }),
      createMessageLifecyclePayload({ id: "msg_1db5887aa00040s2PcEAhH8c7P", role: "user", createdAt: 1794681374642, text: "Do it once more." }),
      createMessageLifecyclePayload({ id: piId("assistant", 1794681374643), role: "assistant", createdAt: 1794681374643 }),
      createMessageLifecyclePayload({ id: piId("assistant", 1794681374683), role: "assistant", createdAt: 1794681374683, text: "Wrote it again." }),
    ]
    const inOrder = conversation.map((message) => message.info.id)

    it("keeps_the_snapshots_order_after_a_reload", () => {
      const state = createSessionStreamState(createSnapshot({ messages: conversation }))

      expect(state.messages.map((m) => m.messageId)).toEqual(inOrder)
    })

    it("keeps_the_order_the_messages_arrive_in_live", () => {
      const state = applyEvents(createState(), conversation.map((payload) => ({ type: "message.created" as const, payload })))

      expect(state.messages.map((m) => m.messageId)).toEqual(inOrder)
    })

    it("keeps_the_order_when_the_replies_have_no_time", () => {
      const untimed = conversation.map((message) =>
        message.info.role === "assistant" ? { ...message, info: { ...message.info, time: { created: 0, completed: null } } } : message)

      const state = createSessionStreamState(createSnapshot({ messages: untimed }))

      expect(state.messages.map((m) => m.messageId)).toEqual(inOrder)
    })
  })
})
