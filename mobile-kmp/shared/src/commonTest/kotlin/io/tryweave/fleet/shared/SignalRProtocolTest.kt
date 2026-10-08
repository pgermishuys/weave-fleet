package io.tryweave.fleet.shared

import io.tryweave.fleet.shared.model.FleetJson
import io.tryweave.fleet.shared.model.long
import io.tryweave.fleet.shared.model.obj
import io.tryweave.fleet.shared.model.str
import io.tryweave.fleet.shared.model.string
import io.tryweave.fleet.shared.signalr.HubMessage
import io.tryweave.fleet.shared.signalr.RecordBuffer
import io.tryweave.fleet.shared.signalr.SignalRProtocol
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.jsonObject
import kotlin.test.Test
import kotlin.test.assertEquals
import kotlin.test.assertIs
import kotlin.test.assertNull
import kotlin.test.assertTrue

class SignalRProtocolTest {
    private val rs = SignalRProtocol.RECORD_SEPARATOR

    @Test
    fun handshakeIsJsonProtocolVersionOneThenTheSeparator() {
        assertEquals("{\"protocol\":\"json\",\"version\":1}\u001e", SignalRProtocol.handshake)
    }

    @Test
    fun handshakeReplyIsAcceptedOrNamesTheError() {
        assertNull(SignalRProtocol.handshakeError("{}"))
        assertEquals("Requested protocol 'json' is not available.", SignalRProtocol.handshakeError("{\"error\":\"Requested protocol 'json' is not available.\"}"))
    }

    @Test
    fun oneFrameCanHoldSeveralMessages() {
        val buffer = RecordBuffer()
        val messages = buffer.append("{\"type\":6}$rs{\"type\":1,\"target\":\"Event\",\"arguments\":[]}$rs")
        assertEquals(2, messages.size)
        assertIs<HubMessage.Ping>(SignalRProtocol.parse(messages[0]))
    }

    @Test
    fun aMessageSplitAcrossFramesWaitsForTheRest() {
        val buffer = RecordBuffer()
        assertTrue(buffer.append("{\"type\":3,\"invocationId\":\"0\",").isEmpty())
        val messages = buffer.append("\"result\":{\"ok\":true}}$rs{\"ty")
        assertEquals(1, messages.size)
        val completion = assertIs<HubMessage.Completion>(SignalRProtocol.parse(messages[0]))
        assertEquals("0", completion.invocationId)
        assertEquals(1, buffer.append("pe\":6}$rs").size)
    }

    @Test
    fun aSeparatorInsideAStringIsEscapedSoItNeverSplits() {
        // JSON escapes control characters, so 0x1E can't appear raw inside a message.
        val encoded = SignalRProtocol.invocation("1", "Send", listOf(JsonPrimitive("a\u001eb")))
        assertEquals(1, encoded.count { it == rs })
        assertTrue(encoded.endsWith(rs))
    }

    @Test
    fun invocationCarriesIdTargetAndArguments() {
        val encoded = SignalRProtocol.invocation("7", "SubscribeToSessionAsync", listOf(JsonPrimitive("abc")))
        val json = FleetJson.parseToJsonElement(encoded.trimEnd(rs)).jsonObject
        assertEquals(1, json["type"].long)
        assertEquals("7", json.str("invocationId"))
        assertEquals("SubscribeToSessionAsync", json.str("target"))
        assertEquals("abc", json["arguments"]!!.let { (it as kotlinx.serialization.json.JsonArray)[0].string })
    }

    @Test
    fun serverEventInvocationParses() {
        val raw = """{"type":1,"target":"Event","arguments":["session:s1",42,{"type":"turn.started","properties":{"sessionID":"s1"}}]}"""
        val message = assertIs<HubMessage.Invocation>(SignalRProtocol.parse(raw))
        assertEquals("Event", message.target)
        assertEquals("session:s1", message.arguments[0].string)
        assertEquals(42L, message.arguments[1].long)
        assertEquals("turn.started", message.arguments[2].obj.str("type"))
    }

    @Test
    fun completionWithErrorAndCloseParse() {
        val failed = assertIs<HubMessage.Completion>(SignalRProtocol.parse("""{"type":3,"invocationId":"2","error":"Nope"}"""))
        assertEquals("Nope", failed.error)
        val close = assertIs<HubMessage.Close>(SignalRProtocol.parse("""{"type":7,"error":"Server shutting down","allowReconnect":true}"""))
        assertTrue(close.allowReconnect)
        assertIs<HubMessage.Other>(SignalRProtocol.parse("""{"type":2,"invocationId":"1","item":1}"""))
    }
}
