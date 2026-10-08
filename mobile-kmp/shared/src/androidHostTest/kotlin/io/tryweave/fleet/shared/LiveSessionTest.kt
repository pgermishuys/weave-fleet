package io.tryweave.fleet.shared

import io.tryweave.fleet.shared.model.Credentials
import io.tryweave.fleet.shared.reducer.PhoneBlock
import io.tryweave.fleet.shared.signalr.HubState
import io.tryweave.fleet.shared.store.FleetClient
import io.tryweave.fleet.shared.store.SessionUiState
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.async
import kotlinx.coroutines.asCoroutineDispatcher
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.withContext
import kotlinx.coroutines.withTimeout
import java.util.concurrent.Executors
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertTrue

/**
 * The whole shared stack against a real (scratch) Fleet, the way the apps use it: FleetClient + SessionController on
 * one thread (as on Dispatchers.Main). Sends a prompt and watches the reply stream in through the hand-written SignalR
 * client and the ported reducer, answers a question from the dock, and stays connected past the server's 30 s timeout.
 * Needs FLEET_LIVE_URL, FLEET_LIVE_TOKEN and FLEET_LIVE_TALK_SESSION (a session this test may talk to).
 */
class LiveSessionTest {
    @Test
    fun streamsAReplyAnswersAQuestionAndStaysConnected() = runBlocking {
        val url = System.getenv("FLEET_LIVE_URL") ?: return@runBlocking
        val token = System.getenv("FLEET_LIVE_TOKEN") ?: return@runBlocking
        val sessionId = System.getenv("FLEET_LIVE_TALK_SESSION") ?: return@runBlocking
        val main = Executors.newSingleThreadExecutor().asCoroutineDispatcher()
        val client = FleetClient(Credentials(url, token, "scratch", "live-test"), main)
        val controller = withContext(main) {
            client.start()
            client.openSession(sessionId)
        }
        suspend fun until(seconds: Long, what: String, check: (SessionUiState) -> Boolean): SessionUiState =
            withTimeout(seconds * 1000) { controller.state.first(check) }.also { println("live: $what") }

        until(20, "snapshot loaded") { !it.loading && it.connection == HubState.Connected }

        val prompt = "Hello from the KMP test ${System.currentTimeMillis()}"
        withContext(main) { controller.send(prompt) }
        var sawStreaming = false
        val replied = until(60, "reply streamed and turn ended") { state ->
            val at = state.blocks.indexOfLast { it is PhoneBlock.User && it.text == prompt }
            val after = if (at >= 0) state.blocks.drop(at + 1) else emptyList()
            if (after.isNotEmpty() && state.isWorking) sawStreaming = true
            after.any { it is PhoneBlock.Text && it.text.contains("Here's the plan.") && it.text.contains("run-tests") } && !state.isWorking
        }
        println("live: ${replied.blocks.size} blocks, saw it working mid-reply: $sawStreaming")

        withContext(main) { controller.send("Before you go on: ask-me which cache") }
        val asked = until(60, "question docked") { it.question != null }
        assertEquals(listOf("SQLite", "Redis"), asked.question!!.options)
        withContext(main) { controller.answerDocked("SQLite") }
        until(60, "question answered") { state -> state.question == null && state.blocks.any { it is PhoneBlock.Question && it.answer == "SQLite" } }

        // A command waits for permission (the scratch Fleet has PermissionLevel=ask): Allow once from the dock.
        withContext(main) { controller.send("Great, run-tests please") }
        val ask = until(60, "permission docked") { it.permission != null }.permission!!
        println("live: ask kind=${ask.kind} tool=${ask.tool} title=${ask.title}")
        withContext(main) { controller.replyToDocked(true) }
        until(90, "command ran after Allow once") { state ->
            state.permission == null && !state.isWorking && state.blocks.any { it is PhoneBlock.Text && it.text.contains("All 42 tests pass") }
        }

        // Not looking (the app went to the background): Fleet sends a session_notification on the sessions topic
        // instead, which is what the notification's Allow once answers, over REST, outside the session screen.
        withContext(main) { controller.setFocused(false) }
        delay(500)
        val notification = async {
            withTimeout(60_000) { client.notifications.first { it.sessionId == sessionId && it.kind == "permission" } }
        }
        withContext(main) { controller.send("run-tests one more time") }
        val n = notification.await()
        println("live: notification '${n.title}': ${n.body} (request ${n.requestId})")
        val answered = CompletableDeferred<String?>()
        withContext(main) { client.replyToPermission(sessionId, n.requestId!!, true) { answered.complete(it) } }
        assertEquals(null, answered.await())
        until(90, "command ran after the notification's Allow once") { state ->
            state.permission == null && !state.isWorking && state.blocks.count { it is PhoneBlock.Text && it.text.contains("All 42 tests pass") } >= 2
        }
        withContext(main) { controller.setFocused(true) }

        // Past the server's 30 s client timeout: only our pings keep the socket open.
        delay(40_000)
        assertEquals(HubState.Connected, client.connection.value)
        withContext(main) { controller.send("Still there?") }
        until(60, "reply after 40 s idle") { state ->
            val at = state.blocks.indexOfLast { it is PhoneBlock.User && it.text == "Still there?" }
            at >= 0 && state.blocks.drop(at + 1).any { it is PhoneBlock.Text } && !state.isWorking
        }
        assertTrue(client.connection.value == HubState.Connected)
        withContext(main) {
            controller.close()
            client.close()
        }
        delay(500)
        main.close()
    }
}
