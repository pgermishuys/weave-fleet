package io.tryweave.fleet.shared.model

import kotlinx.serialization.Serializable
import kotlinx.serialization.json.JsonElement

@Serializable
data class SessionTime(val created: Long? = null, val updated: Long? = null)

@Serializable
data class SessionInfo(val id: String, val title: String = "", val time: SessionTime? = null)

/** One row of `GET /api/sessions` (SessionListResponse). Only what the phone shows. */
@Serializable
data class SessionListItem(
    val session: SessionInfo,
    val instanceId: String? = null,
    val workspaceDirectory: String? = null,
    val workspaceDisplayName: String? = null,
    val activityStatus: String? = null,
    val lifecycleStatus: String? = null,
    val retentionStatus: String? = null,
    val branch: String? = null,
    val projectName: String? = null,
    val harnessType: String? = null,
    val parentSessionId: String? = null,
    val isHidden: Boolean = false,
) {
    val id: String get() = session.id
    val title: String get() = session.title.ifBlank { "Untitled session" }
    /** "demo-app" from the workspace, as the desktop labels a session's folder. */
    val folder: String get() = workspaceDisplayName ?: workspaceDirectory?.trimEnd('/')?.substringAfterLast('/') ?: ""
}

/** `POST /api/sessions/{id}/prompt` (SendPromptApiRequest). */
@Serializable
data class SendPromptRequest(
    val text: String,
    val correlationId: String,
    val agent: String? = null,
    val model: String? = null,
    val attachments: List<JsonElement>? = null,
    val userMessageId: String? = null,
    val effort: String? = null,
)

/** An agent's ask the user answers (`GET /api/sessions/{id}/permissions`). */
@Serializable
data class PermissionAsk(
    val id: String,
    val sessionId: String,
    val kind: String = "other",
    val tool: String = "",
    val title: String? = null,
    val detail: String? = null,
    val directory: String? = null,
    val always: List<String> = emptyList(),
    val callId: String? = null,
    val subagent: String? = null,
    val askedAt: String = "",
) {
    /** "Run a command", as the phone heads the card. */
    val heading: String get() = when (kind) {
        "shell" -> "Run a command"
        "edit" -> "Edit a file"
        "read" -> "Read a file"
        "web" -> "Open a web page"
        else -> "Use ${tool.ifBlank { "a tool" }}"
    }
}

@Serializable
data class PermissionReplyRequest(val reply: String, val message: String? = null)

@Serializable
data class QuestionAnswerRequest(val answers: List<List<String>>)

/** `once`, `always` or `reject` (PermissionReplies in WeaveFleet.Domain). */
enum class PermissionReply(val wire: String) { Once("once"), Always("always"), Reject("reject") }

// ── Pairing ────────────────────────────────────────────────────────────────────────────────────────────────

@Serializable
data class PairingRedeemRequest(
    val secret: String? = null,
    val manualCode: String? = null,
    val deviceName: String,
    val platform: String,
)

@Serializable
data class MachineInfo(
    val id: String,
    val name: String,
    val hostName: String? = null,
    val os: String? = null,
    val version: String? = null,
)

@Serializable
data class PairingRedeemResponse(val deviceId: String, val token: String, val machine: MachineInfo)

/** What a pairing QR code carries (version 1), base64url JSON in the fragment of `<url>/pair#p=…`. */
@Serializable
data class PairingPayload(val v: Int, val machineId: String, val machineName: String, val url: String, val secret: String)

/** What the phone keeps after pairing: where the machine is and its device token. Kept in the Keystore / Keychain. */
@Serializable
data class Credentials(val baseUrl: String, val token: String, val machineName: String, val deviceId: String)

/** A `session_notification` on the global sessions topic (SessionNotificationPayload). */
@Serializable
data class SessionNotification(
    val sessionId: String,
    val reason: String = "",
    val title: String = "",
    val body: String = "",
    val kind: String? = null,
    val requestId: String? = null,
    val machineId: String? = null,
    val machineName: String? = null,
)

/** A wire event as the hub sends it, turned into Fleet's domain shape (`toDomainEvent` in use-signalr-socket.ts). */
data class DomainEvent(val type: String, val payload: JsonElement?, val eventId: Long? = null)
