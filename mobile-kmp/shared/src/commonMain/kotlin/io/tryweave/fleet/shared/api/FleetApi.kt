package io.tryweave.fleet.shared.api

import io.ktor.client.HttpClient
import io.ktor.client.call.body
import io.ktor.client.plugins.HttpTimeout
import io.ktor.client.plugins.contentnegotiation.ContentNegotiation
import io.ktor.client.plugins.websocket.WebSockets
import io.ktor.client.request.HttpRequestBuilder
import io.ktor.client.request.get
import io.ktor.client.request.header
import io.ktor.client.request.post
import io.ktor.client.request.setBody
import io.ktor.client.statement.HttpResponse
import io.ktor.client.statement.bodyAsText
import io.ktor.http.ContentType
import io.ktor.http.contentType
import io.ktor.http.encodeURLPathPart
import io.ktor.http.isSuccess
import io.ktor.serialization.kotlinx.json.json
import io.tryweave.fleet.shared.model.Credentials
import io.tryweave.fleet.shared.model.FleetJson
import io.tryweave.fleet.shared.model.PairingPayload
import io.tryweave.fleet.shared.model.PairingRedeemRequest
import io.tryweave.fleet.shared.model.PairingRedeemResponse
import io.tryweave.fleet.shared.model.PermissionAsk
import io.tryweave.fleet.shared.model.PermissionReply
import io.tryweave.fleet.shared.model.PermissionReplyRequest
import io.tryweave.fleet.shared.model.QuestionAnswerRequest
import io.tryweave.fleet.shared.model.SendPromptRequest
import io.tryweave.fleet.shared.model.SessionListItem
import io.tryweave.fleet.shared.platform.httpEngine
import kotlinx.serialization.json.JsonObject
import kotlin.io.encoding.Base64
import kotlin.io.encoding.ExperimentalEncodingApi
import kotlin.random.Random

class FleetApiException(val status: Int, message: String) : Exception(message)

/** The Ktor client every part of the app shares: JSON both ways, WebSockets for the hub. */
fun createHttpClient(): HttpClient = HttpClient(httpEngine()) {
    install(ContentNegotiation) { json(FleetJson) }
    install(WebSockets)
    install(HttpTimeout) {
        connectTimeoutMillis = 10_000
        requestTimeoutMillis = 30_000
    }
    expectSuccess = false
}

/** A short-lived client for one job (a notification action), closed afterwards. */
suspend fun <T> withFleetApi(credentials: Credentials, block: suspend (FleetApi) -> T): T {
    val http = createHttpClient()
    try {
        return block(FleetApi(http, credentials))
    } finally {
        http.close()
    }
}

/** Fleet's REST API, the few endpoints the phone needs. Every call sends the device token as a bearer token. */
class FleetApi(private val http: HttpClient, private val credentials: Credentials) {
    private val base = credentials.baseUrl.trimEnd('/')

    private fun HttpRequestBuilder.auth() = header("Authorization", "Bearer ${credentials.token}")

    private suspend fun HttpResponse.check(): HttpResponse {
        if (!status.isSuccess()) throw FleetApiException(status.value, "${status.value}: ${bodyAsText().take(300)}")
        return this
    }

    private fun seg(value: String) = value.encodeURLPathPart()

    suspend fun sessions(): List<SessionListItem> = http.get("$base/api/sessions") { auth() }.check().body()

    suspend fun sendPrompt(sessionId: String, text: String): String {
        val correlationId = randomId()
        http.post("$base/api/sessions/${seg(sessionId)}/prompt") {
            auth()
            contentType(ContentType.Application.Json)
            setBody(SendPromptRequest(text = text, correlationId = correlationId))
        }.check()
        return correlationId
    }

    suspend fun permissions(sessionId: String): List<PermissionAsk> =
        http.get("$base/api/sessions/${seg(sessionId)}/permissions") { auth() }.check().body()

    suspend fun replyToPermission(sessionId: String, requestId: String, reply: PermissionReply, message: String? = null) {
        http.post("$base/api/sessions/${seg(sessionId)}/permissions/${seg(requestId)}") {
            auth()
            contentType(ContentType.Application.Json)
            setBody(PermissionReplyRequest(reply.wire, message))
        }.check()
    }

    suspend fun answerQuestion(sessionId: String, requestId: String, answers: List<List<String>>) {
        http.post("$base/api/sessions/${seg(sessionId)}/questions/${seg(requestId)}/answer") {
            auth()
            contentType(ContentType.Application.Json)
            setBody(QuestionAnswerRequest(answers))
        }.check()
    }

    /** The session's messages as the REST API pages them: what a notification action reads without the hub. */
    suspend fun messages(sessionId: String, limit: Int = 30): JsonObject =
        http.get("$base/api/sessions/${seg(sessionId)}/messages?limit=$limit") { auth() }.check().body()

    companion object {
        /**
         * Redeems a pairing code for a device token: the typed 8-character code (`XXXX-XXXX`) or the secret from a
         * QR code. `baseUrl` is the machine's address.
         */
        suspend fun pair(http: HttpClient, baseUrl: String, manualCode: String?, secret: String?, deviceName: String, platform: String): Credentials {
            val base = baseUrl.trim().trimEnd('/')
            val response = http.post("$base/api/pairing/redeem") {
                contentType(ContentType.Application.Json)
                setBody(PairingRedeemRequest(secret = secret, manualCode = manualCode?.trim()?.uppercase(), deviceName = deviceName, platform = platform))
            }
            if (!response.status.isSuccess()) {
                val reason = runCatching { FleetJson.parseToJsonElement(response.bodyAsText()) }.getOrNull()
                throw FleetApiException(response.status.value, (reason as? JsonObject)?.get("error")?.toString()?.trim('"') ?: "That code didn't work (${response.status.value}).")
            }
            val redeemed: PairingRedeemResponse = response.body()
            return Credentials(base, redeemed.token, redeemed.machine.name, redeemed.deviceId)
        }

        /** Reads a pairing QR URL, `https://<machine>/pair#p=<base64url(JSON)>`. Null when it isn't one. */
        @OptIn(ExperimentalEncodingApi::class)
        fun parsePairingUrl(url: String): PairingPayload? {
            val fragment = url.substringAfter("#", "").split('&').firstOrNull { it.startsWith("p=") }?.removePrefix("p=") ?: return null
            return runCatching {
                val json = Base64.UrlSafe.withPadding(Base64.PaddingOption.ABSENT_OPTIONAL).decode(fragment).decodeToString()
                FleetJson.decodeFromString(PairingPayload.serializer(), json).takeIf { it.v == 1 }
            }.getOrNull()
        }

        fun randomId(): String = buildString { repeat(16) { append("0123456789abcdef"[Random.nextInt(16)]) } }
    }
}
