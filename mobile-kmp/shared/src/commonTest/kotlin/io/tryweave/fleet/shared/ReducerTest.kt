package io.tryweave.fleet.shared

import io.tryweave.fleet.shared.model.DomainEvent
import io.tryweave.fleet.shared.model.FleetJson
import io.tryweave.fleet.shared.model.Role
import io.tryweave.fleet.shared.model.TextPart
import io.tryweave.fleet.shared.model.ToolPart
import io.tryweave.fleet.shared.reducer.PhoneBlock
import io.tryweave.fleet.shared.reducer.SessionStreamState
import io.tryweave.fleet.shared.reducer.StepResult
import io.tryweave.fleet.shared.reducer.StreamStatus
import io.tryweave.fleet.shared.reducer.applyDomainEvent
import io.tryweave.fleet.shared.reducer.compareMessageIds
import io.tryweave.fleet.shared.reducer.createSessionStreamState
import io.tryweave.fleet.shared.reducer.foldMessages
import io.tryweave.fleet.shared.reducer.pendingQuestion
import io.tryweave.fleet.shared.reducer.visibleSteps
import kotlinx.serialization.json.jsonObject
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertIs
import kotlin.test.assertNotNull
import kotlin.test.assertNull
import kotlin.test.assertTrue

class ReducerTest {
    private fun obj(json: String) = FleetJson.parseToJsonElement(json).jsonObject
    private fun ev(type: String, json: String) = DomainEvent(type, FleetJson.parseToJsonElement(json))
    private fun SessionStreamState.apply(type: String, json: String) = applyDomainEvent(this, ev(type, json))

    private val snapshot = obj(
        """
        {"session":{"id":"s1","title":"Fix pagination","status":"active"},
         "activityStatus":"idle","lastEventId":12,"hasMore":false,"cursor":null,"isPartial":false,"delegations":[],
         "messages":[
           {"info":{"id":"msg_002","role":"assistant","sessionID":"s1","time":{"created":2,"completed":3}},
            "parts":[{"id":"p2","sessionID":"s1","type":"text","text":"Here's the plan."}]},
           {"info":{"id":"msg_001","role":"user","sessionID":"s1","time":{"created":1}},
            "parts":[{"id":"p1","sessionID":"s1","type":"text","text":"Fix the off-by-one"}]}
         ]}
        """,
    )

    @Test
    fun snapshotBuildsMessagesInIdOrder() {
        val state = createSessionStreamState(snapshot)
        assertEquals(listOf("msg_001", "msg_002"), state.messages.map { it.messageId })
        assertEquals(Role.User, state.messages[0].role)
        assertEquals(12L, state.lastEventId)
        assertEquals(StreamStatus.Idle, state.sessionStatus)
    }

    @Test
    fun countedIdsSortByNumberNotText() {
        assertTrue(compareMessageIds("msg_abc_6", "msg_abc_17") < 0)
        assertTrue(compareMessageIds("msg_a", "msg_b") < 0)
    }

    @Test
    fun deltasStreamIntoTheirPartAndOffsetsDropRepeats() {
        var state = createSessionStreamState(snapshot)
            .apply("turn.started", """{"sessionID":"s1","messageID":"msg_003","index":0}""")
        assertEquals(StreamStatus.Busy, state.sessionStatus)
        state = state.apply("message.part.delta.streamed", """{"sessionID":"s1","messageID":"msg_003","partID":"p3","field":"text","delta":"Hel","offset":0}""")
        state = state.apply("message.part.delta.streamed", """{"sessionID":"s1","messageID":"msg_003","partID":"p3","field":"text","delta":"lo","offset":3}""")
        // A repeat of a delta the part already has (snapshot taken mid-reply) changes nothing.
        state = state.apply("message.part.delta.streamed", """{"sessionID":"s1","messageID":"msg_003","partID":"p3","field":"text","delta":"lo","offset":3}""")
        assertEquals("Hello", (state.messages.last().parts.single() as TextPart).text)
        assertEquals(Role.Assistant, state.messages.last().role)
        state = state.apply("session.idled", """{"sessionId":"s1"}""")
        assertEquals(StreamStatus.Idle, state.sessionStatus)
    }

    @Test
    fun reasoningDeltasAreIgnoredOnlyTextCounts() {
        val state = createSessionStreamState(snapshot)
            .apply("message.part.delta.streamed", """{"sessionID":"s1","messageID":"msg_003","partID":"p3","field":"reasoning","delta":"hmm"}""")
        assertEquals(2, state.messages.size)
    }

    @Test
    fun toolStateMergesFieldByField() {
        var state = createSessionStreamState(snapshot)
        state = state.apply(
            "message.part.updated",
            """{"sessionID":"s1","part":{"id":"t1","sessionID":"s1","messageID":"msg_003","type":"tool","tool":"bash","callID":"c1",
               "state":{"status":"running","input":{"command":"bun test","description":"Run the tests"}}}}""",
        )
        state = state.apply(
            "message.part.updated",
            """{"sessionID":"s1","part":{"id":"t1","sessionID":"s1","messageID":"msg_003","type":"tool","tool":"bash","callID":"c1",
               "state":{"status":"completed","output":"42 passed"}}}""",
        )
        val tool = state.messages.last().parts.single() as ToolPart
        assertEquals("completed", tool.status)
        assertEquals("bun test", tool.input?.get("command").let { (it as kotlinx.serialization.json.JsonPrimitive).content })
    }

    @Test
    fun messageUpdatedWithoutPartsKeepsStreamedParts() {
        var state = createSessionStreamState(snapshot)
            .apply("message.part.delta.streamed", """{"sessionID":"s1","messageID":"msg_003","partID":"p3","field":"text","delta":"Streaming"}""")
        state = state.apply("message.updated", """{"info":{"id":"msg_003","role":"assistant","sessionID":"s1","time":{"created":4,"completed":5}}}""")
        assertEquals("Streaming", (state.messages.last().parts.single() as TextPart).text)
        assertEquals(5L, state.messages.last().completedAt)
    }

    @Test
    fun snapshotPartsDontShortenTextAlreadyStreamed() {
        var state = createSessionStreamState(snapshot)
            .apply("message.part.delta.streamed", """{"sessionID":"s1","messageID":"msg_002","partID":"p2","field":"text","delta":" More.","offset":16}""")
        state = state.apply("message.updated", """{"info":{"id":"msg_002","role":"assistant","sessionID":"s1","time":{"created":2}},"parts":[{"id":"p2","type":"text","text":"Here's"}]}""")
        assertEquals("Here's the plan. More.", (state.messages[1].parts.single() as TextPart).text)
    }

    @Test
    fun turnFailureLandsOnTheLatestReplyOrGetsItsOwnMessage() {
        val failed = createSessionStreamState(snapshot)
            .apply("turn.started", """{"sessionID":"s1","messageID":"x","index":0}""")
            .apply("turn.failed", """{"sessionID":"s1","messageID":null,"error":{"name":"APIError","message":"Rate limited","isRetryable":false,"kind":"rate_limit"}}""")
        assertEquals("Rate limited", failed.messages.last().turnError?.message)
        assertEquals(StreamStatus.Idle, failed.sessionStatus)

        val withPrompt = createSessionStreamState(snapshot)
            .apply("user.prompt.committed", """{"info":{"id":"msg_004","role":"user","sessionID":"s1","time":{"created":9}},"parts":[{"id":"u4","type":"text","text":"again"}],"correlationId":"c"}""")
            .apply("turn.failed", """{"sessionID":"s1","messageID":"msg_002","error":{"name":"E","message":"Boom","isRetryable":false}}""")
        // msg_002 answered an earlier prompt, so the failure gets a message of its own after the new prompt.
        assertNull(withPrompt.messages[1].turnError)
        assertEquals("Boom", withPrompt.messages.last().turnError?.message)
        val blocks = foldMessages(withPrompt.messages)
        assertIs<PhoneBlock.Error>(blocks.last())
    }

    @Test
    fun aSubAgentsQuestionOutranksWork() {
        val state = createSessionStreamState(snapshot)
            .apply("delegation.created", """{"delegationId":"d1","parentSessionId":"s1","parentToolCallId":"c9","childSessionId":"child","title":"Explore","status":"running","createdAt":"x"}""")
        assertEquals(StreamStatus.Delegating, state.sessionStatus)
        val waiting = state.apply("activity_status", """{"sessionId":"child","activityStatus":"waiting_input"}""")
        assertEquals(StreamStatus.WaitingInput, waiting.sessionStatus)
    }

    @Test
    fun toolRunsFoldIntoOneBlockWithASummary() {
        var state = createSessionStreamState(snapshot)
        fun tool(id: String, tool: String, status: String, input: String) = state.apply(
            "message.part.updated",
            """{"sessionID":"s1","part":{"id":"$id","sessionID":"s1","messageID":"msg_005","type":"tool","tool":"$tool","callID":"c$id","state":{"status":"$status","input":$input}}}""",
        )
        state = tool("a", "read", "completed", """{"filePath":"src/page.ts"}""")
        state = tool("b", "read", "completed", """{"filePath":"src/page.test.ts"}""")
        state = tool("c", "bash", "running", """{"command":"bun test"}""")
        val steps = foldMessages(state.messages).last() as PhoneBlock.Steps
        assertEquals("Read 2 files · ran 1 command", steps.summary)
        assertTrue(steps.running)
        assertEquals("src/page.ts", steps.steps[0].detail)
        assertEquals("bun test", steps.steps[2].detail)
        assertEquals(StepResult.Running, steps.steps[2].result)
        assertEquals(3 to 0, visibleSteps(steps.steps).let { it.first.size to it.second.size })
        assertEquals(3 to 2, visibleSteps(List(5) { it }).let { it.first.size to it.second.size })
    }

    @Test
    fun aRunningQuestionIsPendingUntilAnswered() {
        val asked = createSessionStreamState(snapshot).apply(
            "message.part.updated",
            """{"sessionID":"s1","part":{"id":"q1","sessionID":"s1","messageID":"msg_006","type":"tool","tool":"question","callID":"call_q",
               "state":{"status":"running","input":{"questions":[{"header":"Approach","question":"Which fix?","options":[{"label":"Clamp","description":""},{"label":"Rewrite","description":""}]}]}}}}""",
        )
        val pending = assertNotNull(pendingQuestion(asked.messages))
        assertEquals("call_q", pending.requestId)
        assertEquals(listOf("Clamp", "Rewrite"), pending.options)
        assertTrue((foldMessages(asked.messages).last() as PhoneBlock.Question).pending)

        val answered = asked.apply(
            "message.part.updated",
            """{"sessionID":"s1","part":{"id":"q1","sessionID":"s1","messageID":"msg_006","type":"tool","tool":"question","callID":"call_q",
               "state":{"status":"completed","metadata":{"answers":[["Clamp"]]}}}}""",
        )
        assertNull(pendingQuestion(answered.messages))
        assertEquals("Clamp", (foldMessages(answered.messages).last() as PhoneBlock.Question).answer)
    }
}
