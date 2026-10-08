package io.tryweave.fleet.shared.store

import io.tryweave.fleet.shared.api.FleetApi
import io.tryweave.fleet.shared.api.createHttpClient
import io.tryweave.fleet.shared.model.Credentials
import io.tryweave.fleet.shared.model.DomainEvent
import io.tryweave.fleet.shared.model.FleetJson
import io.tryweave.fleet.shared.model.PermissionAsk
import io.tryweave.fleet.shared.model.PermissionReply
import io.tryweave.fleet.shared.reducer.PendingQuestion
import io.tryweave.fleet.shared.reducer.createSessionStreamState
import io.tryweave.fleet.shared.reducer.pendingQuestion
import io.tryweave.fleet.shared.model.SessionListItem
import io.tryweave.fleet.shared.model.SessionNotification
import io.tryweave.fleet.shared.model.long
import io.tryweave.fleet.shared.model.obj
import io.tryweave.fleet.shared.model.str
import io.tryweave.fleet.shared.model.string
import io.tryweave.fleet.shared.signalr.HubConnection
import io.tryweave.fleet.shared.signalr.HubState
import kotlinx.coroutines.CoroutineDispatcher
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.SharedFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asSharedFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.combine
import kotlinx.coroutines.flow.distinctUntilChanged
import kotlinx.coroutines.flow.map
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive

/** A hub event and the topic it came on (`sessions`, or `session:<id>`). */
data class HubEvent(val topic: String, val event: DomainEvent)

/** What a "Needs you" card shows: the permission ask, or the question, the session waits on. */
data class NeedsYou(val permission: PermissionAsk?, val question: PendingQuestion?)

data class SessionsUiState(
    val machineName: String,
    val connection: HubState,
    val loading: Boolean,
    val needsYou: List<SessionListItem>,
    val working: List<SessionListItem>,
    val other: List<SessionListItem>,
    val error: String?,
    val details: Map<String, NeedsYou> = emptyMap(),
)

/**
 * One paired machine: its REST API, its hub connection, and the session list kept live from the `sessions` topic.
 * Lives as long as the app; both UIs hold one.
 */
class FleetClient(
    val credentials: Credentials,
    dispatcher: CoroutineDispatcher = Dispatchers.Main,
) {
    internal val scope = CoroutineScope(SupervisorJob() + dispatcher)
    private val http = createHttpClient()
    val api = FleetApi(http, credentials)
    private val hub = HubConnection(http, credentials.baseUrl, HUB_PATH, { credentials.token }, scope)

    private val _events = MutableSharedFlow<HubEvent>(extraBufferCapacity = 512)
    val events: SharedFlow<HubEvent> = _events.asSharedFlow()

    private val _notifications = MutableSharedFlow<SessionNotification>(extraBufferCapacity = 16)
    /** `session_notification`s: a session needs you (a permission or a question), finished, or failed. */
    val notifications: SharedFlow<SessionNotification> = _notifications.asSharedFlow()

    val connection: StateFlow<HubState> = hub.state

    private val sessions = MutableStateFlow<List<SessionListItem>>(emptyList())
    private val loading = MutableStateFlow(true)
    private val error = MutableStateFlow<String?>(null)
    private val details = MutableStateFlow<Map<String, NeedsYou>>(emptyMap())
    private val reconnectListeners = mutableListOf<suspend () -> Unit>()
    private val openSessions = mutableMapOf<String, Int>()

    val sessionsState: StateFlow<SessionsUiState> = MutableStateFlow(buildSessions(credentials.machineName, emptyList(), HubState.Disconnected, true, null)).also { out ->
        scope.launch {
            combine(sessions, hub.state, loading, error, details) { list, conn, isLoading, err, more ->
                buildSessions(credentials.machineName, list, conn, isLoading, err).copy(details = more)
            }.collect { out.value = it }
        }
    }.asStateFlow()

    init {
        hub.on("Event") { args ->
            val topic = args.getOrNull(0).string ?: return@on
            val data = args.getOrNull(2).obj ?: return@on
            val eventId = args.getOrNull(1).long?.takeIf { it != 0L }
            val event = DomainEvent(data.str("type") ?: return@on, data["properties"], eventId)
            if (topic == SESSIONS_TOPIC) onSessionsTopic(event)
            _events.tryEmit(HubEvent(topic, event))
        }
        hub.onConnected = {
            runCatching { hub.invoke("SubscribeToSessionsTopicAsync") }
            refreshSessions()
            reconnectListeners.toList().forEach { runCatching { it() } }
        }
    }

    fun start() {
        hub.start()
        // Whenever the set of sessions that need you changes, fetch what each one waits on.
        scope.launch {
            sessions.map { list -> list.filter { it.activityStatus in NEEDS_YOU }.map { it.id }.toSet() }
                .distinctUntilChanged()
                .collect { ids -> loadDetails(ids) }
        }
    }

    private suspend fun loadDetails(ids: Set<String>) {
        details.value = ids.associateWith { id ->
            val permission = runCatching { api.permissions(id) }.getOrNull()?.minByOrNull { it.askedAt }
            NeedsYou(permission, if (permission == null) pendingQuestionOf(id) else null)
        }
    }

    /** The question a session waits on, read from a snapshot (the REST message list doesn't carry tool state). */
    suspend fun pendingQuestionOf(sessionId: String): PendingQuestion? {
        val snapshot = runCatching { subscribeSession(sessionId) }.getOrNull() ?: return null
        if ((openSessions[sessionId] ?: 0) == 0) unsubscribeSession(sessionId)
        return pendingQuestion(createSessionStreamState(snapshot).messages)
    }

    /** Callback form of [pendingQuestionOf], for Swift. */
    fun peekQuestion(sessionId: String, onResult: (PendingQuestion?) -> Unit) {
        scope.launch { onResult(pendingQuestionOf(sessionId)) }
    }

    internal fun retain(sessionId: String) { openSessions[sessionId] = (openSessions[sessionId] ?: 0) + 1 }
    internal fun release(sessionId: String): Boolean {
        val left = (openSessions[sessionId] ?: 1) - 1
        if (left <= 0) openSessions.remove(sessionId) else openSessions[sessionId] = left
        return left <= 0
    }

    fun refreshSessions() {
        scope.launch {
            runCatching { api.sessions() }
                .onSuccess { list ->
                    sessions.value = list.filter { !it.isHidden && it.parentSessionId == null && it.retentionStatus != "archived" }
                    error.value = null
                }
                .onFailure { error.value = it.message ?: "Couldn't load sessions" }
            loading.value = false
        }
    }

    private fun onSessionsTopic(event: DomainEvent) {
        val payload = event.payload.obj
        when (event.type) {
            "activity_status" -> {
                val id = payload.str("sessionId") ?: return
                val status = payload.str("activityStatus") ?: return
                if (sessions.value.none { it.id == id }) refreshSessions()
                else sessions.update { list -> list.map { if (it.id == id) it.copy(activityStatus = status) else it } }
            }
            "session_notification" -> payload?.let {
                runCatching { FleetJson.decodeFromJsonElement(SessionNotification.serializer(), it) }.getOrNull()
            }?.let {
                _notifications.tryEmit(it)
                scope.launch { loadDetails(sessions.value.filter { s -> s.activityStatus in NEEDS_YOU }.map { s -> s.id }.toSet()) }
            }
            "session.started", "session.deleted", "session.archived" -> refreshSessions()
        }
    }

    /** Subscribes the session's topic; returns its SessionSnapshot. */
    internal suspend fun subscribeSession(sessionId: String): JsonObject? =
        hub.invoke("SubscribeToSessionAsync", JsonPrimitive(sessionId)).obj

    internal suspend fun unsubscribeSession(sessionId: String) {
        runCatching { hub.invoke("UnsubscribeFromSessionAsync", JsonPrimitive(sessionId)) }
    }

    /**
     * Tells Fleet whether this phone is looking at the session. Fleet skips the notification for a session someone
     * is watching, so the phone says "not focused" when it goes to the background.
     */
    internal suspend fun setFocus(sessionId: String, focused: Boolean) {
        runCatching { hub.invoke("SetSessionFocusAsync", JsonPrimitive(sessionId), JsonPrimitive(focused)) }
    }

    internal fun onReconnected(listener: suspend () -> Unit): () -> Unit {
        reconnectListeners += listener
        return { reconnectListeners -= listener }
    }

    fun openSession(sessionId: String): SessionController = SessionController(this, sessionId)

    // ── Callback wrappers for Swift (no Flow across the boundary) ──────────────────────────────────────────

    fun currentSessions(): SessionsUiState = sessionsState.value

    fun watchSessions(onChange: (SessionsUiState) -> Unit): WatchHandle =
        WatchHandle(scope.launch { sessionsState.collect { onChange(it) } })

    fun watchNotifications(onNotification: (SessionNotification) -> Unit): WatchHandle =
        WatchHandle(scope.launch { notifications.collect { onNotification(it) } })

    /** Answers a permission ask from outside a session screen (a notification action). */
    fun replyToPermission(sessionId: String, requestId: String, allow: Boolean, onDone: (String?) -> Unit) {
        scope.launch {
            val result = runCatching { api.replyToPermission(sessionId, requestId, if (allow) PermissionReply.Once else PermissionReply.Reject) }
            onDone(result.exceptionOrNull()?.message)
        }
    }

    /** Answers a question with one typed or picked answer (a notification's inline reply). */
    fun answerQuestion(sessionId: String, requestId: String, answer: String, onDone: (String?) -> Unit) {
        scope.launch {
            val result = runCatching { api.answerQuestion(sessionId, requestId, listOf(listOf(answer))) }
            onDone(result.exceptionOrNull()?.message)
        }
    }

    fun close() {
        scope.launch { hub.stop() }.invokeOnCompletion {
            http.close()
            scope.cancel()
        }
    }

    companion object {
        const val HUB_PATH = "/hubs/session-events"
        const val SESSIONS_TOPIC = "sessions"
    }
}

private val NEEDS_YOU = setOf("waiting_input")
private val WORKING = setOf("busy", "working", "retry", "delegating")

private fun buildSessions(machineName: String, list: List<SessionListItem>, conn: HubState, loading: Boolean, error: String?): SessionsUiState {
    val byRecent = list.sortedByDescending { it.session.time?.updated ?: it.session.time?.created ?: 0 }
    return SessionsUiState(
        machineName = machineName,
        connection = conn,
        loading = loading,
        needsYou = byRecent.filter { it.activityStatus in NEEDS_YOU },
        working = byRecent.filter { it.activityStatus in WORKING },
        other = byRecent.filter { it.activityStatus !in NEEDS_YOU && it.activityStatus !in WORKING },
        error = error,
    )
}
