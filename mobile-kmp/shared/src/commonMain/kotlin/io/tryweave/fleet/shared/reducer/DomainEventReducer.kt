package io.tryweave.fleet.shared.reducer

import io.tryweave.fleet.shared.model.Delegation
import io.tryweave.fleet.shared.model.DomainEvent
import io.tryweave.fleet.shared.model.FleetJson
import io.tryweave.fleet.shared.model.Message
import io.tryweave.fleet.shared.model.Role
import io.tryweave.fleet.shared.model.TurnError
import io.tryweave.fleet.shared.model.arr
import io.tryweave.fleet.shared.model.bool
import io.tryweave.fleet.shared.model.child
import io.tryweave.fleet.shared.model.long
import io.tryweave.fleet.shared.model.obj
import io.tryweave.fleet.shared.model.str
import kotlinx.serialization.json.JsonObject

/**
 * Port of client/src/lib/domain-event-reducer.ts: `createSessionStreamState` + `applyDomainEvent`.
 *
 * Ported: message.created / message.updated / user.prompt.committed, message.part.updated,
 * message.part.delta.streamed (text field), turn.started / turn.ended / turn.failed, session.idled,
 * delegation.created / updated / completed (for status only), activity_status (a sub-agent's).
 * Skipped: work.*, context.updated (no running-work strip or context meter on this phone), and everything the
 * reducer itself ignores (canvas.*, files.changed, terminal.*, session.queue, ...).
 */

enum class ExplicitStatus { Idle, Busy, Retry }

enum class StreamStatus { Idle, Busy, Retry, Delegating, WaitingInput }

data class SessionStreamState(
    val messages: List<Message> = emptyList(),
    val delegations: List<Delegation> = emptyList(),
    val explicitStatus: ExplicitStatus = ExplicitStatus.Idle,
    val sessionStatus: StreamStatus = StreamStatus.Idle,
    val lastEventId: Long? = null,
) {
    /** Anything but idle is the agent at work (a turn, a sub-agent, a retry, a sub-agent's question). */
    val isWorking: Boolean get() = sessionStatus != StreamStatus.Idle
}

private val ACTIVE_DELEGATION = setOf("pending", "running")

fun toExplicitStatus(activityStatus: String?): ExplicitStatus = when (activityStatus) {
    "busy", "working" -> ExplicitStatus.Busy
    "retry" -> ExplicitStatus.Retry
    else -> ExplicitStatus.Idle
}

/** Builds the state from a SessionSnapshot (the reply to SubscribeToSessionAsync). */
fun createSessionStreamState(snapshot: JsonObject): SessionStreamState {
    val explicit = toExplicitStatus(snapshot.str("activityStatus"))
    val delegations = snapshot["delegations"].arr.orEmpty().mapNotNull { it.obj?.let(::toDelegation) }
    val base = SessionStreamState(
        delegations = delegations,
        explicitStatus = explicit,
        sessionStatus = deriveSnapshotStatus(explicit, delegations),
        lastEventId = snapshot["lastEventId"].long ?: snapshot["lastSequenceNumber"].long,
    )
    return snapshot["messages"].arr.orEmpty().fold(base) { state, message ->
        applyDomainEvent(state, DomainEvent("message.created", message))
    }
}

fun applyDomainEvent(state: SessionStreamState, event: DomainEvent): SessionStreamState {
    val payload = event.payload.obj
    return when (event.type) {
        "message.created", "message.updated", "user.prompt.committed" ->
            if (payload == null) state else state.copy(messages = applyMessageLifecycle(state.messages, payload))

        "message.part.updated" -> {
            val part = payload.child("part") ?: return state
            state.copy(messages = applyPartUpdate(state.messages, part))
        }

        "message.part.delta.streamed" -> {
            if (payload == null || payload.str("field") != "text") return state
            state.copy(
                messages = applyTextDelta(
                    state.messages,
                    payload.str("messageID") ?: return state,
                    payload.str("partID") ?: return state,
                    payload.str("sessionID") ?: "",
                    payload.str("delta") ?: return state,
                    payload["offset"].long,
                ),
            )
        }

        "turn.started" -> state.withExplicit(ExplicitStatus.Busy)
        "turn.ended", "session.idled" -> state.withExplicit(ExplicitStatus.Idle)
        // The harness has used up its own retries: the session stops looking busy now.
        "turn.failed" -> if (payload == null) state.withExplicit(ExplicitStatus.Idle) else
            state.withExplicit(ExplicitStatus.Idle).copy(messages = applyTurnFailure(state.messages, payload))

        "delegation.created", "delegation.updated", "delegation.completed" -> {
            val delegation = payload?.let(::toDelegation) ?: return state
            val at = state.delegations.indexOfFirst { it.delegationId == delegation.delegationId }
            val next = if (at == -1) state.delegations + delegation else state.delegations.toMutableList().apply {
                set(at, delegation.copy(childActivityStatus = this[at].childActivityStatus))
            }
            state.withDelegations(next)
        }

        "activity_status" -> {
            val sessionId = payload.str("sessionId") ?: return state
            val status = payload.str("activityStatus") ?: return state
            var changed = false
            val next = state.delegations.map {
                if (it.childSessionId != sessionId || it.childActivityStatus == status) it
                else { changed = true; it.copy(childActivityStatus = status) }
            }
            if (changed) state.withDelegations(next) else state
        }

        else -> state
    }
}

/** The state with its status from the session list's activity status (the sessions topic keeps it current). */
fun SessionStreamState.withActivityStatus(activityStatus: String): SessionStreamState = withExplicit(toExplicitStatus(activityStatus))

private fun applyMessageLifecycle(messages: List<Message>, payload: JsonObject): List<Message> {
    val info = payload.child("info") ?: return messages
    // Live message.updated can carry the harness's payload, which has no parts; the parts already held are kept.
    return mergeMessageUpdate(ensureMessage(messages, info), info, payload.partsList())
}

/**
 * Puts the failure on the latest turn's reply; when the turn died before producing one, the failure gets a message
 * of its own, so a failed turn never shows nothing.
 */
private fun applyTurnFailure(messages: List<Message>, payload: JsonObject): List<Message> {
    val error = payload.child("error")?.let { runCatching { FleetJson.decodeFromJsonElement(TurnError.serializer(), it) }.getOrNull() }
        ?: TurnError(message = "The turn failed.")
    val named = payload.str("messageID")?.let { id -> messages.indexOfFirst { it.messageId == id } } ?: -1
    val candidate = if (named != -1) named else messages.indexOfLast { it.role == Role.Assistant }
    val lastUser = messages.indexOfLast { it.role == Role.User }
    val target = if (candidate > lastUser) candidate else -1
    if (target == -1) {
        val sessionId = payload.str("sessionID") ?: ""
        return messages + Message(
            messageId = "turn-failed-$sessionId-${messages.size}",
            sessionId = sessionId,
            role = Role.Assistant,
            turnError = error,
        )
    }
    return messages.toMutableList().apply { set(target, this[target].copy(turnError = error)) }
}

private fun toDelegation(raw: JsonObject): Delegation? {
    val id = raw.str("delegationId") ?: return null
    return Delegation(
        delegationId = id,
        parentToolCallId = raw.str("parentToolCallId"),
        childSessionId = raw.str("childSessionId"),
        title = raw.str("title") ?: "",
        status = raw.str("status")?.takeIf { it in setOf("pending", "running", "completed", "error", "cancelled") } ?: "pending",
        childActivityStatus = raw.str("childActivityStatus"),
        background = raw["background"].bool == true,
    )
}

private fun isWaiting(d: Delegation) = d.status in ACTIVE_DELEGATION && d.childActivityStatus == "waiting_input"
private fun hasActive(ds: List<Delegation>) = ds.any { it.status in ACTIVE_DELEGATION && !it.background }

private fun deriveStatus(explicit: ExplicitStatus, ds: List<Delegation>): StreamStatus = when {
    ds.any(::isWaiting) -> StreamStatus.WaitingInput
    explicit == ExplicitStatus.Busy -> StreamStatus.Busy
    explicit == ExplicitStatus.Retry -> StreamStatus.Retry
    hasActive(ds) -> StreamStatus.Delegating
    else -> StreamStatus.Idle
}

private fun deriveSnapshotStatus(explicit: ExplicitStatus, ds: List<Delegation>): StreamStatus =
    if (hasActive(ds) && ds.none(::isWaiting)) StreamStatus.Delegating else deriveStatus(explicit, ds)

private fun SessionStreamState.withExplicit(explicit: ExplicitStatus) =
    copy(explicitStatus = explicit, sessionStatus = deriveStatus(explicit, delegations))

private fun SessionStreamState.withDelegations(ds: List<Delegation>) =
    copy(delegations = ds, sessionStatus = deriveStatus(explicitStatus, ds))
