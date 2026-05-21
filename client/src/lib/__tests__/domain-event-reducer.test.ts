import { afterEach, describe, expect, it, vi } from "vitest"
import { nextTick } from "vue"
import { useDraftState } from "@/composables/use-draft-state"
import { useSendPrompt, useSentPrompts, confirmSentPrompt } from "@/composables/use-send-prompt"
import { apiFetch } from "@/lib/api-client"
import type { DelegationDto } from "@/lib/api-types"

vi.mock("@/lib/api-client", () => ({
  apiFetch: vi.fn(),
}))

vi.mock("@/composables/use-agents", async () => {
  const { computed, ref } = await import("vue")
  const agents = ref([{ id: "agent-1", name: "Loom", description: "" }])
  const agentsById = ref({ "agent-1": agents.value[0] })

  return {
    useAgents: () => ({
      agents,
      agentsById,
      defaultAgentId: computed(() => "agent-1"),
      isLoading: ref(false),
      error: ref(undefined),
      refresh: vi.fn(),
    }),
  }
})

vi.mock("@/composables/use-models", async () => {
  const { computed, ref } = await import("vue")
  const model = { id: "model-1", name: "Model", providerId: "provider-1", selectionKey: "model-key", provider: "Provider", description: "" }
  const models = ref([model])
  const modelsByKey = ref({ "model-key": model })

  return {
    useModels: () => ({
      models,
      modelsByKey,
      defaultModelKey: computed(() => "model-key"),
      isLoading: ref(false),
      error: ref(undefined),
      refresh: vi.fn(),
    }),
  }
})

afterEach(() => {
  vi.mocked(apiFetch).mockReset()
  vi.restoreAllMocks()
})
import { applyDomainEvent, createSessionStreamState, type SessionStreamState } from "@/lib/domain-event-reducer"
import type { DomainEvent, MessageLifecyclePayload } from "@/lib/domain-events"
import type { SessionSnapshot, SessionSnapshotDelegation } from "@/lib/session-snapshot"

function createDelegation(overrides: Partial<DelegationDto> = {}): DelegationDto {
  return {
    delegationId: "delegation-1",
    parentToolCallId: "tool-1",
    childSessionId: "child-1",
    title: "Delegate work",
    status: "running",
    createdAt: "2026-01-01T00:00:00Z",
    ...overrides,
  }
}

function createSnapshotDelegation(overrides: Partial<SessionSnapshotDelegation> = {}): SessionSnapshotDelegation {
  return {
    delegationId: "delegation-1",
    parentToolCallId: "tool-1",
    childSessionId: "child-1",
    title: "Delegate work",
    status: "running",
    createdAt: "2026-01-01T00:00:00Z",
    ...overrides,
  }
}

function applyDelegationCreatedEvent(
  state: SessionStreamState,
  overrides: Partial<{
    delegationId: string
    parentSessionId: string
    parentToolCallId: string | null
    childSessionId: string | null
    title: string
    status: string
    createdAt: string
  }> = {},
): SessionStreamState {
  return applyDomainEvent(state, {
    type: "delegation.created",
    payload: {
      delegationId: "delegation-1",
      parentSessionId: "session-1",
      parentToolCallId: "tool-1",
      childSessionId: "child-1",
      title: "Delegate work",
      status: "running",
      createdAt: "2026-01-01T00:00:00Z",
      ...overrides,
    },
  })
}

function applyDelegationCompletedEvent(
  state: SessionStreamState,
  overrides: Partial<{
    delegationId: string
    parentSessionId: string
    parentToolCallId: string | null
    childSessionId: string | null
    title: string
    status: string
    createdAt: string
    completedAt: string
  }> = {},
): SessionStreamState {
  return applyDomainEvent(state, {
    type: "delegation.completed",
    payload: {
      delegationId: "delegation-1",
      parentSessionId: "session-1",
      parentToolCallId: "tool-1",
      childSessionId: "child-1",
      title: "Delegate work",
      status: "completed",
      createdAt: "2026-01-01T00:00:00Z",
      completedAt: "2026-01-01T00:01:00Z",
      ...overrides,
    },
  })
}

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
    ...overrides,
  }
}

function createMessageLifecyclePayload(
  overrides: Partial<MessageLifecyclePayload["info"]> & {
    id: string
    role: string
    createdAt: number
    completedAt?: number | null
    text?: string
    partId?: string
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
        created: overrides.createdAt,
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
    explicitStatus: "idle",
    sessionStatus: "idle",
    lastEventId: null,
    ...overrides,
  }
}

function applyEvents(state: SessionStreamState, events: DomainEvent[]): SessionStreamState {
  return events.reduce((currentState, event) => applyDomainEvent(currentState, event), state)
}

function createJsonResponse<T>(body: T, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "Content-Type": "application/json" },
  })
}

function deferResponse(): { promise: Promise<Response>; resolve: (value: Response) => void } {
  let resolve!: (value: Response) => void
  const promise = new Promise<Response>((resolvePromise) => {
    resolve = resolvePromise
  })

  return { promise, resolve }
}

describe("domain-event-reducer", () => {
  it("keeps_user_prompt_before_assistant_response_when_lifecycle_events_have_equal_timestamps", () => {
    const userPrompt = createMessageLifecyclePayload({
      id: "user-message-1",
      role: "user",
      createdAt: 1000,
      text: "Explain why stream ordering matters.",
    })
    const assistantCreated = createMessageLifecyclePayload({
      id: "assistant-message-1",
      role: "assistant",
      createdAt: 1000,
    })
    const assistantUpdated = createMessageLifecyclePayload({
      id: "assistant-message-1",
      role: "assistant",
      createdAt: 1000,
      completedAt: 2000,
      text: "The assistant response is now complete.",
      partId: "assistant-text-1",
    })
    const initialState = createSessionStreamState(createSnapshot({
      messages: [userPrompt],
    }))

    const createdState = applyDomainEvent(initialState, {
      type: "message.created",
      payload: assistantCreated,
    })
    const streamedState = applyDomainEvent(createdState, {
      type: "message.part.delta.streamed",
      payload: {
        sessionID: "session-1",
        messageID: "assistant-message-1",
        partID: "assistant-text-1",
        field: "text",
        delta: "The assistant response is now partial.",
      },
    })
    const updatedState = applyDomainEvent(streamedState, {
      type: "message.updated",
      payload: assistantUpdated,
    })

    expect(updatedState.messages.filter((message) => message.role === "user")).toHaveLength(1)
    expect(updatedState.messages.filter((message) => message.role === "assistant")).toHaveLength(1)
    expect(updatedState.messages.map((message) => message.role)).toEqual(["user", "assistant"])

    const assistantMessage = updatedState.messages.find((message) => message.role === "assistant")
    expect(assistantMessage?.parts).toEqual([
      {
        partId: "assistant-text-1",
        type: "text",
        text: "The assistant response is now complete.",
      },
    ])
  })

  it("does_not_duplicate_assistant_message_when_part_update_arrives_before_lifecycle_events", () => {
    const userPrompt = createMessageLifecyclePayload({
      id: "user-message-1",
      role: "user",
      createdAt: 1000,
      text: "Explain why out-of-order events must merge.",
    })
    const assistantCreated = createMessageLifecyclePayload({
      id: "assistant-message-1",
      role: "assistant",
      createdAt: 1000,
      text: "Partial response from the part update with extra streamed text.",
      partId: "assistant-text-1",
    })
    const assistantUpdated = createMessageLifecyclePayload({
      id: "assistant-message-1",
      role: "assistant",
      createdAt: 1000,
      completedAt: 2000,
      text: "Partial response from the part update.",
      partId: "assistant-text-1",
    })
    const initialState = createSessionStreamState(createSnapshot({
      messages: [userPrompt],
    }))

    const partUpdatedState = applyDomainEvent(initialState, {
      type: "message.part.updated",
      payload: {
        sessionID: "session-1",
        part: {
          id: "assistant-text-1",
          sessionID: "session-1",
          messageID: "assistant-message-1",
          type: "text",
          text: "Partial response from the part update with extra streamed text.",
        },
      },
    })
    const createdState = applyDomainEvent(partUpdatedState, {
      type: "message.created",
      payload: assistantCreated,
    })
    const updatedState = applyDomainEvent(createdState, {
      type: "message.updated",
      payload: assistantUpdated,
    })

    expect(updatedState.messages.filter((message) => message.messageId === "assistant-message-1")).toHaveLength(1)
    expect(updatedState.messages.filter((message) => message.role === "user")).toHaveLength(1)
    expect(updatedState.messages.filter((message) => message.role === "assistant")).toHaveLength(1)
    expect(updatedState.messages.map((message) => message.role)).toEqual(["user", "assistant"])
    expect(updatedState.messages.map((message) => message.messageId)).toEqual(["user-message-1", "assistant-message-1"])

    const assistantMessage = updatedState.messages.find((message) => message.messageId === "assistant-message-1")
    expect(assistantMessage).toMatchObject({
      role: "assistant",
      createdAt: 1000,
      completedAt: 2000,
    })
    expect(assistantMessage?.parts).toEqual([
      {
        partId: "assistant-text-1",
        type: "text",
        text: "Partial response from the part update with extra streamed text.",
      },
    ])
  })

  it("reconciles_text_delta_placeholder_with_lifecycle_without_duplicating_final_snapshot_text", () => {
    const assistantCreated = createMessageLifecyclePayload({
      id: "assistant-message-1",
      role: "assistant",
      createdAt: 1000,
      text: "Streamed response text.",
      partId: "assistant-text-1",
    })
    const assistantUpdated = createMessageLifecyclePayload({
      id: "assistant-message-1",
      role: "assistant",
      createdAt: 1000,
      completedAt: 2000,
      text: "Streamed response text.",
      partId: "assistant-text-1",
    })

    const streamedState = applyDomainEvent(createState(), {
      type: "message.part.delta.streamed",
      payload: {
        sessionID: "session-1",
        messageID: "assistant-message-1",
        partID: "assistant-text-1",
        field: "text",
        delta: "Streamed response text.",
      },
    })
    const createdState = applyDomainEvent(streamedState, {
      type: "message.created",
      payload: assistantCreated,
    })
    const updatedState = applyDomainEvent(createdState, {
      type: "message.updated",
      payload: assistantUpdated,
    })

    expect(updatedState.messages.filter((message) => message.messageId === "assistant-message-1")).toHaveLength(1)

    const assistantMessage = updatedState.messages.find((message) => message.messageId === "assistant-message-1")
    expect(assistantMessage).toMatchObject({
      messageId: "assistant-message-1",
      sessionId: "session-1",
      role: "assistant",
      createdAt: 1000,
      completedAt: 2000,
    })
    expect(assistantMessage?.parts).toEqual([
      {
        partId: "assistant-text-1",
        type: "text",
        text: "Streamed response text.",
      },
    ])
  })

  it("preserves_longer_streamed_text_when_shorter_final_lifecycle_snapshot_arrives", () => {
    const assistantCreated = createMessageLifecyclePayload({
      id: "assistant-message-1",
      role: "assistant",
      createdAt: 1000,
      text: "Streamed response text with additional delta words.",
      partId: "assistant-text-1",
    })
    const assistantUpdated = createMessageLifecyclePayload({
      id: "assistant-message-1",
      role: "assistant",
      createdAt: 1000,
      completedAt: 2000,
      text: "Streamed response text.",
      partId: "assistant-text-1",
    })

    const streamedState = applyDomainEvent(createState(), {
      type: "message.part.delta.streamed",
      payload: {
        sessionID: "session-1",
        messageID: "assistant-message-1",
        partID: "assistant-text-1",
        field: "text",
        delta: "Streamed response text with additional delta words.",
      },
    })
    const createdState = applyDomainEvent(streamedState, {
      type: "message.created",
      payload: assistantCreated,
    })
    const updatedState = applyDomainEvent(createdState, {
      type: "message.updated",
      payload: assistantUpdated,
    })

    expect(updatedState.messages.filter((message) => message.messageId === "assistant-message-1")).toHaveLength(1)

    const assistantMessage = updatedState.messages.find((message) => message.messageId === "assistant-message-1")
    expect(assistantMessage).toMatchObject({
      role: "assistant",
      createdAt: 1000,
      completedAt: 2000,
    })
    expect(assistantMessage?.parts).toEqual([
      {
        partId: "assistant-text-1",
        type: "text",
        text: "Streamed response text with additional delta words.",
      },
    ])
  })

  it("keeps_live_stream_and_reconnect_snapshot_plus_pending_events_equivalent_without_duplicate_messages", () => {
    const userPrompt = createMessageLifecyclePayload({
      id: "user-message-1",
      role: "user",
      createdAt: 1000,
      text: "Explain reconnect parity.",
      partId: "user-text-1",
    })
    const assistantCreated = createMessageLifecyclePayload({
      id: "assistant-message-1",
      role: "assistant",
      createdAt: 1000,
      text: "Reconnect ",
      partId: "assistant-text-1",
    })
    const assistantCompleted = createMessageLifecyclePayload({
      id: "assistant-message-1",
      role: "assistant",
      createdAt: 1000,
      completedAt: 2000,
      text: "Reconnect parity is preserved.",
      partId: "assistant-text-1",
      cost: 0.042,
      tokens: {
        input: 17,
        output: 23,
        reasoning: 5,
      },
    })
    const pendingEvents: DomainEvent[] = [
      {
        type: "message.part.delta.streamed",
        payload: {
          sessionID: "session-1",
          messageID: "assistant-message-1",
          partID: "assistant-text-1",
          field: "text",
          delta: "parity is preserved.",
        },
      },
      {
        type: "message.updated",
        payload: assistantCompleted,
      },
    ]

    const liveState = applyEvents(createState(), [
      {
        type: "message.created",
        payload: userPrompt,
      },
      {
        type: "message.created",
        payload: assistantCreated,
      },
      ...pendingEvents,
    ])
    const reconnectedState = applyEvents(createSessionStreamState(createSnapshot({
      messages: [userPrompt, assistantCreated],
      lastEventId: 2,
    })), pendingEvents)

    expect(reconnectedState.messages).toEqual(liveState.messages)
    expect(reconnectedState.messages.map((message) => message.role)).toEqual(["user", "assistant"])
    expect(reconnectedState.messages.map((message) => message.parts)).toEqual(liveState.messages.map((message) => message.parts))
    expect(reconnectedState.messages.map((message) => ({
      cost: message.cost,
      tokens: message.tokens,
      completedAt: message.completedAt,
    }))).toEqual(liveState.messages.map((message) => ({
      cost: message.cost,
      tokens: message.tokens,
      completedAt: message.completedAt,
    })))
    expect(reconnectedState.messages.filter((message) => message.role === "user")).toHaveLength(1)
    expect(reconnectedState.messages.filter((message) => message.role === "assistant")).toHaveLength(1)
    expect(new Set(reconnectedState.messages.map((message) => message.messageId)).size).toBe(reconnectedState.messages.length)
  })

  it("reconciles_optimistic_prompt_when_committed_event_has_matching_correlation_id", async () => {
    const sessionId = "session-prompt-reconcile"
    const { sentPrompts, hasPendingPrompts } = useSentPrompts(sessionId)
    const promptRequest = deferResponse()
    vi.mocked(apiFetch).mockReturnValueOnce(promptRequest.promise)
    vi.spyOn(crypto, "randomUUID")
      .mockReturnValueOnce("optimistic-id" as `${string}-${string}-${string}-${string}-${string}`)
      .mockReturnValueOnce("corr-reconcile" as `${string}-${string}-${string}-${string}-${string}`)
    useDraftState(sessionId, { agentId: "", modelId: "" }).setText("Ship the feature")
    useSendPrompt(sessionId).sendPrompt()
    await nextTick()

    expect(sentPrompts.value).toHaveLength(1)
    expect(sentPrompts.value[0]).toMatchObject({
      id: "user-optimisticid",
      correlationId: "prompt-corrreconcile",
      status: "pending",
    })

    const state = applyDomainEvent(createState(), {
      type: "user.prompt.committed",
      eventId: 31,
      payload: {
        correlationId: "prompt-corrreconcile",
        ...createMessageLifecyclePayload({
          id: "user-server-id",
          role: "user",
          sessionID: sessionId,
          createdAt: 1_000,
          text: "Ship the feature",
        }),
      },
    })

    expect(state.messages).toHaveLength(1)
    expect(state.messages[0]?.messageId).toBe("user-server-id")
    expect(sentPrompts.value).toHaveLength(1)
    expect(sentPrompts.value[0]).toMatchObject({
      id: "user-server-id",
      serverMessageId: "user-server-id",
      correlationId: "prompt-corrreconcile",
      eventId: 31,
      status: "confirmed",
    })
    expect(hasPendingPrompts.value).toBe(false)

    promptRequest.resolve(createJsonResponse({ eventId: 31, correlationId: "prompt-corrreconcile" }))
  })

  it("confirming_same_correlation_id_twice_is_idempotent", async () => {
    const sessionId = "session-prompt-idempotent"
    const { sentPrompts, hasPendingPrompts } = useSentPrompts(sessionId)
    const promptRequest = deferResponse()
    vi.mocked(apiFetch).mockReturnValueOnce(promptRequest.promise)
    vi.spyOn(crypto, "randomUUID")
      .mockReturnValueOnce("optimistic-id" as `${string}-${string}-${string}-${string}-${string}`)
      .mockReturnValueOnce("corr-idempotent" as `${string}-${string}-${string}-${string}-${string}`)
    useDraftState(sessionId, { agentId: "", modelId: "" }).setText("Do it once")
    useSendPrompt(sessionId).sendPrompt()
    await nextTick()

    confirmSentPrompt(sessionId, {
      correlationId: "prompt-corridempotent",
      eventId: 31,
      serverMessageId: "user-server-id",
    })
    confirmSentPrompt(sessionId, {
      correlationId: "prompt-corridempotent",
      eventId: 31,
      serverMessageId: "user-server-id",
    })

    expect(sentPrompts.value).toHaveLength(1)
    expect(sentPrompts.value[0]).toMatchObject({
      id: "user-server-id",
      serverMessageId: "user-server-id",
      correlationId: "prompt-corridempotent",
      eventId: 31,
      status: "confirmed",
    })
    expect(hasPendingPrompts.value).toBe(false)

    promptRequest.resolve(createJsonResponse({ eventId: 31, correlationId: "prompt-corridempotent" }))
  })

  it("derives delegating status from idle snapshots with active delegations", () => {
    const state = createSessionStreamState(createSnapshot({
      delegations: [createSnapshotDelegation()],
    }))

    expect(state.explicitStatus).toBe("idle")
    expect(state.sessionStatus).toBe("delegating")
  })

  it("hydrates as delegating from busy snapshots with active delegations", () => {
    const state = createSessionStreamState(createSnapshot({
      activityStatus: "busy",
      delegations: [createSnapshotDelegation({ status: "pending" })],
    }))

    expect(state.explicitStatus).toBe("busy")
    expect(state.sessionStatus).toBe("delegating")
  })

  it("keeps busy status while turns are active even with delegations", () => {
    const state = applyDomainEvent(createState({
      delegations: [createDelegation()],
      sessionStatus: "delegating",
    }), {
      type: "turn.started",
      payload: {
        sessionID: "session-1",
        messageID: "message-1",
        index: 0,
        agent: null,
        modelID: null,
        parentID: null,
      },
    })

    expect(state.explicitStatus).toBe("busy")
    expect(state.sessionStatus).toBe("busy")
  })

  it("enters delegating when an active delegation is created while explicitly idle", () => {
    const state = applyDelegationCreatedEvent(createState())

    expect(state.explicitStatus).toBe("idle")
    expect(state.sessionStatus).toBe("delegating")
  })

  it("keeps a session non-idle when a turn ends with an active delegation", () => {
    const state = applyDomainEvent(createState({
      delegations: [createDelegation({ status: "pending" })],
      explicitStatus: "busy",
      sessionStatus: "busy",
    }), {
      type: "turn.ended",
      payload: {
        sessionID: "session-1",
        messageID: "message-1",
        index: 0,
        reason: null,
        cost: 0,
        tokens: null,
        completedAt: null,
      },
    })

    expect(state.explicitStatus).toBe("busy")
    expect(state.sessionStatus).toBe("busy")
  })

  it("stays delegating after explicit idle until the final active delegation completes", () => {
    const idledState = applyDomainEvent(createState({
      delegations: [createDelegation()],
      explicitStatus: "busy",
      sessionStatus: "busy",
    }), {
      type: "session.idled",
      payload: {
        sessionId: "session-1",
      },
    })

    expect(idledState.explicitStatus).toBe("idle")
    expect(idledState.sessionStatus).toBe("delegating")

    const completedState = applyDelegationCompletedEvent(idledState)

    expect(completedState.explicitStatus).toBe("idle")
    expect(completedState.sessionStatus).toBe("idle")
  })

  it("returns to idle when the final active delegation completes", () => {
    const state = applyDelegationCompletedEvent(createState({
      delegations: [createDelegation()],
      sessionStatus: "delegating",
    }))

    expect(state.explicitStatus).toBe("idle")
    expect(state.sessionStatus).toBe("idle")
  })

  it("stays busy when the final active delegation completes before explicit idle", () => {
    const state = applyDelegationCompletedEvent(createState({
      delegations: [createDelegation()],
      explicitStatus: "busy",
      sessionStatus: "busy",
    }))

    expect(state.explicitStatus).toBe("busy")
    expect(state.sessionStatus).toBe("busy")
  })

  it("does not return to idle until all active delegations are terminal", () => {
    const stateWithDelegations = createState({
      delegations: [
        createDelegation({ delegationId: "delegation-1", parentToolCallId: "tool-1", childSessionId: "child-1", status: "running" }),
        createDelegation({ delegationId: "delegation-2", parentToolCallId: "tool-2", childSessionId: "child-2", status: "pending" }),
        createDelegation({ delegationId: "delegation-3", parentToolCallId: "tool-3", childSessionId: "child-3", status: "completed" }),
      ],
      explicitStatus: "idle",
      sessionStatus: "delegating",
    })

    const afterFirstCompletion = applyDelegationCompletedEvent(stateWithDelegations, {
      delegationId: "delegation-1",
      parentToolCallId: "tool-1",
      childSessionId: "child-1",
    })

    expect(afterFirstCompletion.sessionStatus).toBe("delegating")

    const afterSecondCompletion = applyDelegationCompletedEvent(afterFirstCompletion, {
      delegationId: "delegation-2",
      parentToolCallId: "tool-2",
      childSessionId: "child-2",
    })

    expect(afterSecondCompletion.explicitStatus).toBe("idle")
    expect(afterSecondCompletion.sessionStatus).toBe("idle")
  })

  it("treats error and cancelled delegations as terminal states", () => {
    const afterError = applyDelegationCompletedEvent(createState({
      delegations: [createDelegation()],
      explicitStatus: "idle",
      sessionStatus: "delegating",
    }), {
      status: "error",
    })

    expect(afterError.sessionStatus).toBe("idle")

    const afterCancelled = applyDelegationCompletedEvent(createState({
      delegations: [createDelegation()],
      explicitStatus: "idle",
      sessionStatus: "delegating",
    }), {
      status: "cancelled",
    })

    expect(afterCancelled.sessionStatus).toBe("idle")
  })

  it("becomes idle when a session idles with no delegations", () => {
    const state = applyDomainEvent(createState({
      explicitStatus: "busy",
      sessionStatus: "busy",
    }), {
      type: "session.idled",
      payload: {
        sessionId: "session-1",
      },
    })

    expect(state.explicitStatus).toBe("idle")
    expect(state.sessionStatus).toBe("idle")
  })

  it("initializes as delegating from an idle snapshot when any delegation is active", () => {
    const state = createSessionStreamState(createSnapshot({
      delegations: [
        createSnapshotDelegation({ delegationId: "delegation-1", status: "completed" }),
        createSnapshotDelegation({ delegationId: "delegation-2", parentToolCallId: "tool-2", childSessionId: "child-2", status: "running" }),
      ],
    }))

    expect(state.explicitStatus).toBe("idle")
    expect(state.sessionStatus).toBe("delegating")
  })

  it("does not oscillate to idle when the child idles before parent delegation completion", () => {
    const withDelegation = applyDelegationCreatedEvent(createState({
      explicitStatus: "busy",
      sessionStatus: "busy",
    }))

    expect(withDelegation.sessionStatus).toBe("busy")

    const afterChildIdle = applyDomainEvent(withDelegation, {
      type: "session.idled",
      payload: {
        sessionId: "child-1",
      },
    })

    expect(afterChildIdle.explicitStatus).toBe("idle")
    expect(afterChildIdle.sessionStatus).toBe("delegating")
    expect(afterChildIdle.delegations[0]?.status).toBe("running")

    const afterParentTurnEnded = applyDomainEvent(afterChildIdle, {
      type: "turn.ended",
      payload: {
        sessionID: "session-1",
        messageID: "message-1",
        index: 0,
        reason: null,
        cost: 0,
        tokens: null,
        completedAt: null,
      },
    })

    expect(afterParentTurnEnded.explicitStatus).toBe("idle")
    expect(afterParentTurnEnded.sessionStatus).toBe("delegating")

    const afterParentDelegationCompleted = applyDelegationCompletedEvent(afterParentTurnEnded)

    expect(afterParentDelegationCompleted.explicitStatus).toBe("idle")
    expect(afterParentDelegationCompleted.sessionStatus).toBe("idle")
  })
})
