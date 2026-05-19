import { describe, expect, it } from "vitest"
import type { DelegationDto } from "@/lib/api-types"
import { applyDomainEvent, createSessionStreamState, type SessionStreamState } from "@/lib/domain-event-reducer"
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
    lastSequenceNumber: 1,
    hasMore: false,
    cursor: null,
    ...overrides,
  }
}

function createState(overrides: Partial<SessionStreamState> = {}): SessionStreamState {
  return {
    messages: [],
    delegations: [],
    explicitStatus: "idle",
    sessionStatus: "idle",
    lastSequenceNumber: null,
    ...overrides,
  }
}

describe("domain-event-reducer", () => {
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
