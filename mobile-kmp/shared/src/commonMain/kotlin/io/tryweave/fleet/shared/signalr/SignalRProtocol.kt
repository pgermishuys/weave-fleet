package io.tryweave.fleet.shared.signalr

import io.tryweave.fleet.shared.model.FleetJson
import io.tryweave.fleet.shared.model.arr
import io.tryweave.fleet.shared.model.bool
import io.tryweave.fleet.shared.model.long
import io.tryweave.fleet.shared.model.str
import kotlinx.serialization.json.JsonArray
import kotlinx.serialization.json.JsonElement
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.put

/**
 * The SignalR JSON hub protocol, version 1: just enough of it for Fleet's session hub. Every message is a JSON
 * object followed by the record separator (0x1E); one WebSocket frame may hold several, or half of one.
 * See https://github.com/dotnet/aspnetcore/blob/main/src/SignalR/docs/specs/HubProtocol.md
 */
object SignalRProtocol {
    const val RECORD_SEPARATOR: Char = '\u001e'

    const val INVOCATION = 1
    const val STREAM_ITEM = 2
    const val COMPLETION = 3
    const val PING = 6
    const val CLOSE = 7

    val handshake: String = "{\"protocol\":\"json\",\"version\":1}$RECORD_SEPARATOR"
    val ping: String = "{\"type\":6}$RECORD_SEPARATOR"

    fun invocation(invocationId: String?, target: String, arguments: List<JsonElement>): String {
        val message = buildJsonObject {
            put("type", INVOCATION)
            if (invocationId != null) put("invocationId", invocationId)
            put("target", target)
            put("arguments", JsonArray(arguments))
        }
        return FleetJson.encodeToString(JsonObject.serializer(), message) + RECORD_SEPARATOR
    }

    /** The server's handshake reply: `{}` when accepted, `{"error":"…"}` when not. Returns the error, or null. */
    fun handshakeError(message: String): String? =
        runCatching { FleetJson.parseToJsonElement(message).jsonObject.str("error") }.getOrElse { "Bad handshake reply: $message" }

    fun parse(message: String): HubMessage {
        val json = FleetJson.parseToJsonElement(message).jsonObject
        return when (json["type"].long?.toInt()) {
            INVOCATION -> HubMessage.Invocation(
                target = json.str("target") ?: "",
                arguments = json["arguments"].arr?.toList() ?: emptyList(),
                invocationId = json.str("invocationId"),
            )
            COMPLETION -> HubMessage.Completion(
                invocationId = json.str("invocationId") ?: "",
                result = json["result"],
                error = json.str("error"),
            )
            PING -> HubMessage.Ping
            CLOSE -> HubMessage.Close(json.str("error"), json["allowReconnect"].bool == true)
            else -> HubMessage.Other(json["type"].long?.toInt() ?: -1)
        }
    }
}

sealed interface HubMessage {
    data class Invocation(val target: String, val arguments: List<JsonElement>, val invocationId: String?) : HubMessage
    data class Completion(val invocationId: String, val result: JsonElement?, val error: String?) : HubMessage
    data object Ping : HubMessage
    data class Close(val error: String?, val allowReconnect: Boolean) : HubMessage
    data class Other(val type: Int) : HubMessage
}

/** Splits incoming text into whole messages, holding a partial one until the rest arrives. */
class RecordBuffer {
    private val pending = StringBuilder()

    fun append(text: String): List<String> {
        pending.append(text)
        val out = mutableListOf<String>()
        while (true) {
            val end = pending.indexOf(SignalRProtocol.RECORD_SEPARATOR)
            if (end < 0) break
            out += pending.substring(0, end)
            pending.deleteRange(0, end + 1)
        }
        return out
    }
}
