package io.tryweave.fleet.shared.store

import io.tryweave.fleet.shared.model.DomainEvent
import io.tryweave.fleet.shared.model.FleetJson
import io.tryweave.fleet.shared.model.PermissionAsk
import io.tryweave.fleet.shared.model.PermissionReply
import io.tryweave.fleet.shared.model.obj
import io.tryweave.fleet.shared.model.str
import io.tryweave.fleet.shared.reducer.PendingQuestion
import io.tryweave.fleet.shared.reducer.PhoneBlock
import io.tryweave.fleet.shared.reducer.PhoneItem
import io.tryweave.fleet.shared.reducer.groupTools
import io.tryweave.fleet.shared.reducer.SessionStreamState
import io.tryweave.fleet.shared.reducer.StreamStatus
import io.tryweave.fleet.shared.reducer.applyDomainEvent
import io.tryweave.fleet.shared.reducer.createSessionStreamState
import io.tryweave.fleet.shared.reducer.foldMessages
import io.tryweave.fleet.shared.reducer.pendingQuestion
import io.tryweave.fleet.shared.reducer.withActivityStatus
import io.tryweave.fleet.shared.signalr.HubState
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch
import kotlin.time.Clock
import kotlin.time.ExperimentalTime

data class SessionUiState(
    val sessionId: String,
    val title: String,
    val folder: String,
    val loading: Boolean = true,
    val blocks: List<PhoneBlock> = emptyList(),
    /** [blocks] with tool runs grouped into boxes: what the conversation draws. */
    val items: List<PhoneItem> = emptyList(),
    val status: StreamStatus = StreamStatus.Idle,
    /** The oldest permission ask waiting on you; it takes the composer's place. */
    val permission: PermissionAsk? = null,
    val question: PendingQuestion? = null,
    val connection: HubState = HubState.Disconnected,
    val sending: Boolean = false,
    val error: String? = null,
    /** When the current turn started (epoch ms), for "Working · 1m 20s"; null when idle. */
    val workingSinceMs: Long? = null,
) {
    val isWorking: Boolean get() = status != StreamStatus.Idle
    /** What the phone headline says: "Needs you" beats "Working". */
    val needsYou: Boolean get() = permission != null || question != null || status == StreamStatus.WaitingInput
}

/**
 * The session screen's state holder (a ViewModel without the Android dependency): subscribes the session's topic,
 * builds the conversation from the snapshot, applies live events through the ported reducer, keeps the pending
 * permission asks, and sends prompts and answers.
 */
@OptIn(ExperimentalTime::class)
class SessionController internal constructor(private val client: FleetClient, val sessionId: String) {
    private var stream = SessionStreamState()
    private var permissions = listOf<PermissionAsk>()
    // Events that arrive while the snapshot is on its way are held and applied after it (the reducer drops repeats).
    private var held: MutableList<DomainEvent>? = null
    private var focused = true

    private val item get() = client.sessionsState.value.let { s -> (s.needsYou + s.working + s.other).firstOrNull { it.id == sessionId } }

    private val _state = MutableStateFlow(SessionUiState(sessionId, item?.title ?: "Session", item?.folder ?: ""))
    val state: StateFlow<SessionUiState> = _state.asStateFlow()

    private val jobs = mutableListOf<kotlinx.coroutines.Job>()
    private val removeReconnect: () -> Unit

    init {
        jobs += client.scope.launch {
            client.events.collect { (topic, event) ->
                when (topic) {
                    "session:$sessionId" -> onSessionEvent(event)
                    FleetClient.SESSIONS_TOPIC -> if (event.type == "activity_status" && event.payload.obj.str("sessionId") == sessionId) {
                        event.payload.obj.str("activityStatus")?.let { stream = stream.withActivityStatus(it); publish() }
                    } else if (event.type == "activity_status") {
                        stream = applyDomainEvent(stream, event); publish() // a sub-agent's status
                    }
                }
            }
        }
        jobs += client.scope.launch { client.connection.collect { conn -> _state.update { it.copy(connection = conn) } } }
        client.retain(sessionId)
        removeReconnect = client.onReconnected { subscribe() }
        if (client.connection.value == HubState.Connected) client.scope.launch { subscribe() }
    }

    private suspend fun subscribe() {
        held = mutableListOf()
        val snapshot = runCatching { client.subscribeSession(sessionId) }
        val missed = held.orEmpty()
        held = null
        snapshot.onSuccess { json ->
            if (json != null) {
                stream = createSessionStreamState(json)
                json["session"].obj.str("title")?.takeIf { it.isNotBlank() }?.let { t -> _state.update { it.copy(title = t) } }
            }
            missed.forEach { stream = applyDomainEvent(stream, it) }
            _state.update { it.copy(loading = false, error = null) }
        }.onFailure { e -> _state.update { it.copy(loading = false, error = e.message) } }
        runCatching { client.api.permissions(sessionId) }.onSuccess { permissions = it }
        publish()
        // Focus only counts for a subscribed session, so it's said again after every (re)subscribe.
        if (focused) client.setFocus(sessionId, true)
    }

    /** On screen or not: the UI calls this when the app goes to the background and comes back. */
    fun setFocused(focused: Boolean) {
        if (this.focused == focused) return
        this.focused = focused
        client.scope.launch { client.setFocus(sessionId, focused) }
    }

    private fun onSessionEvent(event: DomainEvent) {
        held?.let { if (!event.type.startsWith("permission.")) { it += event; return } }
        when (event.type) {
            "permission.asked" -> event.payload.obj?.let {
                runCatching { FleetJson.decodeFromJsonElement(PermissionAsk.serializer(), it) }.getOrNull()
            }?.let { ask -> permissions = permissions.filter { it.id != ask.id } + ask }
            "permission.replied" -> event.payload.obj.str("id")?.let { id -> permissions = permissions.filter { it.id != id } }
            else -> stream = applyDomainEvent(stream, event)
        }
        publish()
    }

    private fun publish() {
        val now = Clock.System.now().toEpochMilliseconds()
        _state.update { current ->
            val working = stream.sessionStatus != StreamStatus.Idle
            val blocks = foldMessages(stream.messages)
            current.copy(
                blocks = blocks,
                items = groupTools(blocks),
                status = stream.sessionStatus,
                permission = permissions.minByOrNull { it.askedAt },
                question = pendingQuestion(stream.messages),
                workingSinceMs = if (working) current.workingSinceMs ?: now else null,
            )
        }
    }

    fun send(text: String) {
        val trimmed = text.trim()
        if (trimmed.isEmpty()) return
        _state.update { it.copy(sending = true, error = null) }
        client.scope.launch {
            val result = runCatching { client.api.sendPrompt(sessionId, trimmed) }
            _state.update { it.copy(sending = false, error = result.exceptionOrNull()?.message) }
        }
    }

    fun reply(ask: PermissionAsk, reply: PermissionReply) {
        permissions = permissions.filter { it.id != ask.id }
        publish()
        client.scope.launch {
            runCatching { client.api.replyToPermission(ask.sessionId, ask.id, reply) }
                .onFailure { e -> _state.update { it.copy(error = e.message) } }
        }
    }

    /** Swift-friendly: allow once or deny the docked ask. */
    fun replyToDocked(allow: Boolean) {
        state.value.permission?.let { reply(it, if (allow) PermissionReply.Once else PermissionReply.Reject) }
    }

    fun answer(question: PendingQuestion, answer: String) {
        client.scope.launch {
            runCatching { client.api.answerQuestion(sessionId, question.requestId, listOf(listOf(answer))) }
                .onFailure { e -> _state.update { it.copy(error = e.message) } }
        }
    }

    fun answerDocked(answer: String) {
        state.value.question?.let { answer(it, answer) }
    }

    /** The state now (Swift reads this once, then [watch]es). */
    fun current(): SessionUiState = state.value

    fun watch(onChange: (SessionUiState) -> Unit): WatchHandle =
        WatchHandle(client.scope.launch { state.collect { onChange(it) } })

    fun close() {
        jobs.forEach { it.cancel() }
        removeReconnect()
        val last = client.release(sessionId)
        client.scope.launch {
            client.setFocus(sessionId, false)
            if (last) client.unsubscribeSession(sessionId)
        }
    }
}
