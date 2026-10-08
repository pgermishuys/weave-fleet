package io.tryweave.fleet.shared

import io.tryweave.fleet.shared.api.createHttpClient
import io.tryweave.fleet.shared.model.obj
import io.tryweave.fleet.shared.reducer.createSessionStreamState
import io.tryweave.fleet.shared.signalr.HubConnection
import io.tryweave.fleet.shared.signalr.HubState
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withTimeout
import kotlinx.serialization.json.JsonPrimitive
import kotlin.test.Test
import kotlin.test.assertTrue

/**
 * Against a real (scratch) Fleet: FLEET_LIVE_URL, FLEET_LIVE_TOKEN and FLEET_LIVE_SESSION. Skipped without them.
 * Proves the hand-written SignalR client handshakes, invokes and parses a real SessionSnapshot.
 */
class LiveHubTest {
    @Test
    fun subscribesAndGetsASnapshot() = live(skipNegotiation = true)

    @Test
    fun negotiatesFirstWhenAsked() = live(skipNegotiation = false)

    private fun live(skipNegotiation: Boolean) = runBlocking {
        val url = System.getenv("FLEET_LIVE_URL") ?: return@runBlocking
        val token = System.getenv("FLEET_LIVE_TOKEN") ?: return@runBlocking
        val session = System.getenv("FLEET_LIVE_SESSION") ?: return@runBlocking
        val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
        val http = createHttpClient()
        val hub = HubConnection(http, url, "/hubs/session-events", { token }, scope, skipNegotiation)
        hub.start()
        withTimeout(15_000) { hub.state.first { it == HubState.Connected } }
        hub.invoke("SubscribeToSessionsTopicAsync")
        val snapshot = hub.invoke("SubscribeToSessionAsync", JsonPrimitive(session)).obj!!
        val state = createSessionStreamState(snapshot)
        println("live snapshot (skipNegotiation=$skipNegotiation): ${state.messages.size} messages, status ${state.sessionStatus}")
        assertTrue(state.messages.isNotEmpty())
        hub.stop()
        http.close()
        scope.cancel()
    }
}
