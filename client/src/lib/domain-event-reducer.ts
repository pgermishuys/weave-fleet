import type { AccumulatedMessage, DelegationDto } from "@/lib/api-types"
import { confirmSentPrompt } from "@/composables/use-send-prompt"
import { applyDelegationCreated, applyDelegationUpdated } from "@/lib/delegation-state"
import type { DelegationCompleted, DelegationCreated, DelegationUpdated, DomainEvent, MessageLifecyclePayload } from "@/lib/domain-events"
import { applyPartUpdate, applyTextDelta, ensureMessage, mergeMessageUpdate } from "@/lib/event-state"
import type { SessionSnapshot, SessionSnapshotDelegation } from "@/lib/session-snapshot"

export type SessionStreamExplicitStatus = "idle" | "busy"

export type SessionStreamStatus = SessionStreamExplicitStatus | "delegating"

export interface SessionStreamState {
  messages: AccumulatedMessage[]
  delegations: DelegationDto[]
  explicitStatus: SessionStreamExplicitStatus
  /**
   * Derived reducer/composable status. This may be tri-state even while
   * downstream list/store activity badges temporarily map "delegating" to
   * busy/active until those consumers become delegation-aware.
   */
  sessionStatus: SessionStreamStatus
  lastEventId: number | null
}

const IDLE_ACTIVITY_STATUSES = new Set(["idle"])
const BUSY_ACTIVITY_STATUSES = new Set(["busy", "working"])
const ACTIVE_DELEGATION_STATUSES = new Set<DelegationDto["status"]>(["pending", "running"])

export function createSessionStreamState(snapshot: SessionSnapshot): SessionStreamState {
  const explicitStatus = toExplicitStatus(snapshot.activityStatus)
  const delegations = snapshot.delegations.map(mapSnapshotDelegation)

  const baseState: SessionStreamState = {
    messages: [],
    delegations,
    explicitStatus,
    sessionStatus: deriveSnapshotSessionStatus(explicitStatus, delegations),
    lastEventId: snapshot.lastEventId ?? snapshot.lastSequenceNumber ?? null,
  }

  return snapshot.messages.reduce<SessionStreamState>(
    (state, message) => applyDomainEvent(state, { type: "message.created", payload: message }),
    baseState,
  )
}

export function applyDomainEvent(state: SessionStreamState, event: DomainEvent): SessionStreamState {
  switch (event.type) {
    case "message.created":
      return {
        ...state,
        messages: applyMessageLifecycle(state.messages, event.payload),
      }

    case "message.updated":
      return {
        ...state,
        messages: applyMessageLifecycle(state.messages, event.payload),
      }

    case "user.prompt.committed":
      confirmSentPrompt(event.payload.info.sessionID, {
        correlationId: event.payload.correlationId ?? undefined,
        eventId: event.eventId,
        serverMessageId: event.payload.info.id,
      })

      return {
        ...state,
        messages: applyMessageLifecycle(state.messages, event.payload),
      }

    case "message.part.updated":
      return {
        ...state,
        messages: applyPartUpdate(state.messages, event.payload.part),
      }

    case "message.part.delta.streamed":
      if (event.payload.field !== "text") {
        return state
      }

      return {
        ...state,
        messages: applyTextDelta(
          state.messages,
          event.payload.messageID,
          event.payload.partID,
          event.payload.sessionID,
          event.payload.delta,
        ),
      }

    case "turn.started":
      return withExplicitStatus(state, "busy")

    case "turn.ended":
      return state

    case "delegation.created":
      return withDelegations(state, applyDelegationCreated(state.delegations, mapDelegationEvent(event)))

    case "delegation.updated":
    case "delegation.completed":
      return withDelegations(state, upsertDelegation(state.delegations, mapDelegationEvent(event)))

    case "session.idled":
      return withExplicitStatus(state, "idle")

    default:
      return state
  }
}

function applyMessageLifecycle(messages: AccumulatedMessage[], payload: MessageLifecyclePayload): AccumulatedMessage[] {
  return mergeMessageUpdate(ensureMessage(messages, payload.info), {
    ...payload.info,
    time: {
      created: payload.info.time.created,
      completed: payload.info.time.completed ?? undefined,
    },
    cost: payload.info.cost ?? undefined,
    tokens: payload.info.tokens ?? undefined,
    parts: payload.parts.map((part) => ({ ...part } as Record<string, unknown>)),
  })
}

function toExplicitStatus(activityStatus: string): SessionStreamExplicitStatus {
  if (BUSY_ACTIVITY_STATUSES.has(activityStatus)) {
    return "busy"
  }

  if (IDLE_ACTIVITY_STATUSES.has(activityStatus)) {
    return "idle"
  }

  return "idle"
}

function deriveSessionStatus(
  explicitStatus: SessionStreamExplicitStatus,
  delegations: DelegationDto[],
): SessionStreamStatus {
  if (explicitStatus === "busy") {
    return "busy"
  }

  if (hasActiveDelegations(delegations)) {
    return "delegating"
  }

  return "idle"
}

function deriveSnapshotSessionStatus(
  explicitStatus: SessionStreamExplicitStatus,
  delegations: DelegationDto[],
): SessionStreamStatus {
  // Snapshot hydration/reconnect can land while the parent activity status still
  // reflects turn work, but active delegations must surface immediately.
  if (hasActiveDelegations(delegations)) {
    return "delegating"
  }

  return deriveSessionStatus(explicitStatus, delegations)
}

function hasActiveDelegations(delegations: DelegationDto[]): boolean {
  return delegations.some((delegation) => ACTIVE_DELEGATION_STATUSES.has(delegation.status))
}

function withExplicitStatus(state: SessionStreamState, explicitStatus: SessionStreamExplicitStatus): SessionStreamState {
  return {
    ...state,
    explicitStatus,
    sessionStatus: deriveSessionStatus(explicitStatus, state.delegations),
  }
}

function withDelegations(state: SessionStreamState, delegations: DelegationDto[]): SessionStreamState {
  return {
    ...state,
    delegations,
    sessionStatus: deriveSessionStatus(state.explicitStatus, delegations),
  }
}

function mapSnapshotDelegation(delegation: SessionSnapshotDelegation): DelegationDto {
  return {
    delegationId: delegation.delegationId,
    parentToolCallId: delegation.parentToolCallId,
    childSessionId: delegation.childSessionId,
    title: delegation.title,
    status: toDelegationStatus(delegation.status),
    createdAt: delegation.createdAt,
  }
}

function mapDelegationEvent(event: DelegationCreated | DelegationUpdated | DelegationCompleted): DelegationDto {
  return {
    delegationId: event.payload.delegationId,
    parentToolCallId: event.payload.parentToolCallId,
    childSessionId: event.payload.childSessionId,
    title: event.payload.title,
    status: toDelegationStatus(event.payload.status),
    createdAt: event.payload.createdAt,
  }
}

function upsertDelegation(delegations: DelegationDto[], delegation: DelegationDto): DelegationDto[] {
  const updated = applyDelegationUpdated(delegations, delegation)
  if (updated !== delegations) {
    return updated
  }

  return applyDelegationCreated(delegations, delegation)
}

function toDelegationStatus(status: string): DelegationDto["status"] {
  switch (status) {
    case "pending":
    case "running":
    case "completed":
    case "error":
    case "cancelled":
      return status
    default:
      return "pending"
  }
}
