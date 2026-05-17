import type { AccumulatedMessage, DelegationDto } from "@/lib/api-types"
import { applyDelegationCreated, applyDelegationUpdated } from "@/lib/delegation-state"
import type { DelegationCompleted, DelegationCreated, DelegationUpdated, DomainEvent, MessageLifecyclePayload } from "@/lib/domain-events"
import { applyPartUpdate, applyTextDelta, ensureMessage, mergeMessageUpdate } from "@/lib/event-state"
import type { SessionSnapshot, SessionSnapshotDelegation } from "@/lib/session-snapshot"

export interface SessionStreamState {
  messages: AccumulatedMessage[]
  delegations: DelegationDto[]
  sessionStatus: "idle" | "busy"
  lastSequenceNumber: number | null
}

const IDLE_ACTIVITY_STATUSES = new Set(["idle"])
const BUSY_ACTIVITY_STATUSES = new Set(["busy", "working"])

export function createSessionStreamState(snapshot: SessionSnapshot): SessionStreamState {
  const baseState: SessionStreamState = {
    messages: [],
    delegations: snapshot.delegations.map(mapSnapshotDelegation),
    sessionStatus: toSessionStatus(snapshot.activityStatus),
    lastSequenceNumber: snapshot.lastSequenceNumber,
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
      return {
        ...state,
        sessionStatus: "busy",
      }

    case "turn.ended":
      return {
        ...state,
        sessionStatus: "idle",
      }

    case "delegation.created":
      return {
        ...state,
        delegations: applyDelegationCreated(state.delegations, mapDelegationEvent(event)),
      }

    case "delegation.updated":
    case "delegation.completed":
      return {
        ...state,
        delegations: upsertDelegation(state.delegations, mapDelegationEvent(event)),
      }

    case "session.started":
      return {
        ...state,
        sessionStatus: "idle",
      }

    case "session.idled":
    case "session.stopped":
    case "session.deleted":
    case "session.archived":
      return {
        ...state,
        sessionStatus: "idle",
      }

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

function toSessionStatus(activityStatus: string): "idle" | "busy" {
  if (BUSY_ACTIVITY_STATUSES.has(activityStatus)) {
    return "busy"
  }

  if (IDLE_ACTIVITY_STATUSES.has(activityStatus)) {
    return "idle"
  }

  return "idle"
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
