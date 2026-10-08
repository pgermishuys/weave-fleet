package io.tryweave.fleet.shared.model

import kotlinx.serialization.Serializable
import kotlinx.serialization.json.JsonObject

/** Port of client-types.ts AccumulatedPart (compaction parts skipped). */
sealed interface Part {
    val partId: String
}

data class TextPart(override val partId: String, val text: String) : Part
data class ReasoningPart(override val partId: String, val text: String, val summary: String? = null) : Part
data class ToolPart(override val partId: String, val tool: String, val callId: String, val state: JsonObject?) : Part {
    val status: String get() = state.str("status") ?: "pending"
    val input: JsonObject? get() = state.child("input")
}
data class FilePart(override val partId: String, val mime: String, val filename: String?, val url: String) : Part

@Serializable
data class TurnError(
    val name: String = "",
    val message: String = "",
    val isRetryable: Boolean = false,
    val kind: String? = null,
    val retryAt: String? = null,
)

enum class Role { User, Assistant, Shell }

/** Port of AccumulatedMessage (cost/tokens, slash commands and compaction summaries left out). */
data class Message(
    val messageId: String,
    val sessionId: String,
    val role: Role,
    val parts: List<Part> = emptyList(),
    val createdAt: Long? = null,
    val completedAt: Long? = null,
    val agent: String? = null,
    val modelID: String? = null,
    val parentID: String? = null,
    val turnError: TurnError? = null,
    val finish: String? = null,
    val steered: Boolean = false,
)

data class Delegation(
    val delegationId: String,
    val parentToolCallId: String?,
    val childSessionId: String?,
    val title: String,
    val status: String,
    val childActivityStatus: String? = null,
    val background: Boolean = false,
)
