package io.tryweave.fleet.shared.signalr

import io.ktor.client.HttpClient
import io.ktor.client.plugins.websocket.DefaultClientWebSocketSession
import io.ktor.client.plugins.websocket.webSocketSession
import io.ktor.client.request.header
import io.ktor.client.request.post
import io.ktor.client.statement.bodyAsText
import io.ktor.http.encodeURLParameter
import io.ktor.http.isSuccess
import io.ktor.websocket.Frame
import io.ktor.websocket.close
import io.ktor.websocket.readText
import io.tryweave.fleet.shared.model.FleetJson
import io.tryweave.fleet.shared.model.str
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Job
import kotlinx.coroutines.coroutineScope
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.withTimeout
import kotlinx.serialization.json.JsonElement
import kotlinx.serialization.json.jsonObject
import kotlin.time.Duration.Companion.seconds
import kotlin.time.TimeSource

enum class HubState { Disconnected, Connecting, Connected, Reconnecting }

class HubException(message: String) : Exception(message)

/**
 * A minimal SignalR client over Ktor WebSockets (Microsoft's own Java client is JVM-only, so it can't run on iOS).
 * JSON protocol only, WebSockets only. It tries the socket straight away (skipNegotiation) and falls back to
 * `POST …/negotiate?negotiateVersion=1` when that is refused. Pings every 15 s as the server expects, treats 30 s of
 * silence as a dead connection, and reconnects with backoff until stopped; [onConnected] runs after every (re)connect
 * so callers can subscribe again.
 */
class HubConnection(
    private val http: HttpClient,
    private val baseUrl: String,
    private val hubPath: String,
    private val accessToken: () -> String?,
    private val scope: CoroutineScope,
    skipNegotiation: Boolean = true,
) {
    private val _state = MutableStateFlow(HubState.Disconnected)
    val state: StateFlow<HubState> = _state.asStateFlow()

    /** Runs after each successful handshake; a failure here doesn't drop the connection. */
    var onConnected: (suspend () -> Unit)? = null

    private val handlers = mutableMapOf<String, (List<JsonElement>) -> Unit>()
    private val pending = mutableMapOf<String, CompletableDeferred<JsonElement?>>()
    private val lock = Mutex()
    private val sendLock = Mutex()
    private var nextId = 0
    private var session: DefaultClientWebSocketSession? = null
    private var loop: Job? = null
    private var negotiate = !skipNegotiation

    /** Registers the handler for a client method the server calls, e.g. `Event`. Register before [start]. */
    fun on(target: String, handler: (List<JsonElement>) -> Unit) {
        handlers[target] = handler
    }

    fun start() {
        if (loop?.isActive == true) return
        loop = scope.launch { runWithReconnect() }
    }

    suspend fun stop() {
        loop?.cancel()
        loop = null
        session?.runCatching { close() }
        session = null
        failPending("Connection stopped")
        _state.value = HubState.Disconnected
    }

    /** Calls a hub method and waits for its result (null for a void method). */
    suspend fun invoke(target: String, vararg arguments: JsonElement): JsonElement? {
        val current = session ?: throw HubException("Not connected")
        val deferred = CompletableDeferred<JsonElement?>()
        val id = lock.withLock {
            val id = (nextId++).toString()
            pending[id] = deferred
            id
        }
        try {
            sendLock.withLock { current.send(Frame.Text(SignalRProtocol.invocation(id, target, arguments.toList()))) }
            return withTimeout(30.seconds) { deferred.await() }
        } finally {
            lock.withLock { pending.remove(id) }
        }
    }

    private suspend fun runWithReconnect() {
        var attempt = 0
        while (scope.isActive) {
            _state.value = if (attempt == 0) HubState.Connecting else HubState.Reconnecting
            try {
                connectOnce { attempt = 0 }
            } catch (e: CancellationException) {
                throw e
            } catch (_: Throwable) {
                // fall through to the backoff
            }
            session = null
            failPending("Connection lost")
            _state.value = HubState.Reconnecting
            delay(RECONNECT_DELAYS_MS[minOf(attempt, RECONNECT_DELAYS_MS.lastIndex)])
            attempt++
        }
    }

    private suspend fun openSocket(): DefaultClientWebSocketSession {
        val token = accessToken()
        val wsBase = baseUrl.trimEnd('/').replaceFirst("http", "ws") + hubPath
        val auth = token?.let { "access_token=${it.encodeURLParameter()}" }
        if (!negotiate) {
            try {
                return http.webSocketSession(wsBase + (auth?.let { "?$it" } ?: ""))
            } catch (e: CancellationException) {
                throw e
            } catch (_: Throwable) {
                negotiate = true // e.g. a proxy that wants the negotiate step: use it from now on
            }
        }
        val response = http.post("${baseUrl.trimEnd('/')}$hubPath/negotiate?negotiateVersion=1") {
            if (token != null) header("Authorization", "Bearer $token")
        }
        if (!response.status.isSuccess()) throw HubException("Negotiate failed: ${response.status}")
        val body = FleetJson.parseToJsonElement(response.bodyAsText()).jsonObject
        val id = body.str("connectionToken") ?: body.str("connectionId") ?: throw HubException("Negotiate gave no connection id")
        return http.webSocketSession("$wsBase?id=${id.encodeURLParameter()}" + (auth?.let { "&$it" } ?: ""))
    }

    private suspend fun connectOnce(onHandshake: () -> Unit) {
        val socket = openSocket()
        session = socket
        val buffer = RecordBuffer()
        var handshakeDone = false
        var lastHeard = TimeSource.Monotonic.markNow()
        socket.send(Frame.Text(SignalRProtocol.handshake))

        coroutineScope {
            val keepAlive = launch {
                while (isActive) {
                    delay(PING_INTERVAL_MS)
                    if (lastHeard.elapsedNow() > SERVER_TIMEOUT) {
                        socket.close()
                        break
                    }
                    sendLock.withLock { socket.send(Frame.Text(SignalRProtocol.ping)) }
                }
            }
            try {
                for (frame in socket.incoming) {
                    val text = when (frame) {
                        is Frame.Text -> frame.readText()
                        is Frame.Binary -> frame.data.decodeToString()
                        else -> continue
                    }
                    lastHeard = TimeSource.Monotonic.markNow()
                    for (message in buffer.append(text)) {
                        if (!handshakeDone) {
                            SignalRProtocol.handshakeError(message)?.let { throw HubException(it) }
                            handshakeDone = true
                            _state.value = HubState.Connected
                            onHandshake()
                            launch { runCatching { onConnected?.invoke() } }
                            continue
                        }
                        dispatch(SignalRProtocol.parse(message))
                    }
                }
            } finally {
                keepAlive.cancel()
            }
        }
    }

    private suspend fun dispatch(message: HubMessage) {
        when (message) {
            is HubMessage.Invocation -> handlers[message.target]?.let { handler ->
                runCatching { handler(message.arguments) }
            }
            is HubMessage.Completion -> lock.withLock { pending.remove(message.invocationId) }?.let { deferred ->
                if (message.error != null) deferred.completeExceptionally(HubException(message.error))
                else deferred.complete(message.result)
            }
            is HubMessage.Close -> throw HubException(message.error ?: "Server closed the connection")
            HubMessage.Ping, is HubMessage.Other -> Unit
        }
    }

    private suspend fun failPending(reason: String) {
        val all = lock.withLock { pending.values.toList().also { pending.clear() } }
        all.forEach { it.completeExceptionally(HubException(reason)) }
    }

    companion object {
        private val RECONNECT_DELAYS_MS = longArrayOf(0, 2_000, 5_000, 10_000, 30_000)
        private const val PING_INTERVAL_MS = 15_000L
        private val SERVER_TIMEOUT = 30.seconds
    }
}
