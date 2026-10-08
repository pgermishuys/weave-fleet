package io.tryweave.fleet.shared.reducer

import io.tryweave.fleet.shared.model.FilePart
import io.tryweave.fleet.shared.model.FleetJson
import io.tryweave.fleet.shared.model.Message
import io.tryweave.fleet.shared.model.Part
import io.tryweave.fleet.shared.model.ReasoningPart
import io.tryweave.fleet.shared.model.Role
import io.tryweave.fleet.shared.model.TextPart
import io.tryweave.fleet.shared.model.ToolPart
import io.tryweave.fleet.shared.model.TurnError
import io.tryweave.fleet.shared.model.arr
import io.tryweave.fleet.shared.model.bool
import io.tryweave.fleet.shared.model.child
import io.tryweave.fleet.shared.model.num
import io.tryweave.fleet.shared.model.obj
import io.tryweave.fleet.shared.model.str
import kotlinx.serialization.json.JsonObject

/**
 * Port of client/src/lib/event-state.ts: pure helpers that accumulate events into renderable messages. Same
 * semantics as the TypeScript (sorted insertion by message id, snapshot parts merged so streamed text isn't lost,
 * tool state merged field by field, deltas applied from their offset).
 */

private val COUNTED_ID = Regex("^(.*)_(\\d+)$")

fun toMessageRole(role: String?): Role = when (role) {
    "user" -> Role.User
    "shell" -> Role.Shell
    else -> Role.Assistant
}

/** Orders message ids as text, except `msg_<stem>_6` before `msg_<stem>_17` (a fork's copies). */
fun compareMessageIds(a: String, b: String): Int {
    val left = COUNTED_ID.matchEntire(a)
    val right = COUNTED_ID.matchEntire(b)
    if (left != null && right != null && left.groupValues[1] == right.groupValues[1]) {
        return left.groupValues[2].toBigNumber().compareTo(right.groupValues[2].toBigNumber())
    }
    return a.compareTo(b).coerceIn(-1, 1)
}

private fun String.toBigNumber(): Double = toDoubleOrNull() ?: 0.0

/** Inserts by message id (binary search); ids without the `msg_` prefix go to the end. */
internal fun insertMessageSorted(messages: List<Message>, newMsg: Message): List<Message> {
    val newId = newMsg.messageId
    if (!newId.startsWith("msg_")) return messages + newMsg
    var left = 0
    var right = messages.size
    while (left < right) {
        val mid = (left + right) / 2
        val midId = messages[mid].messageId
        if (!midId.startsWith("msg_")) {
            right = mid
            continue
        }
        if (compareMessageIds(midId, newId) < 0) left = mid + 1 else right = mid
    }
    return messages.toMutableList().apply { add(left, newMsg) }
}

fun ensureMessage(prev: List<Message>, info: JsonObject): List<Message> {
    val messageId = info.str("id") ?: return prev
    if (prev.any { it.messageId == messageId }) return prev
    val role = toMessageRole(info.str("role"))
    return insertMessageSorted(
        prev,
        Message(
            messageId = messageId,
            sessionId = info.str("sessionID") ?: "",
            role = role,
            createdAt = info.child("time").num("created"),
            agent = info.str("agent"),
            modelID = info.str("modelID"),
            parentID = info.str("parentID"),
            steered = info["steered"].bool == true,
        ),
    )
}

internal fun readTurnError(info: JsonObject?): TurnError? {
    val raw = info.child("turnError") ?: return null
    val error = runCatching { FleetJson.decodeFromJsonElement(TurnError.serializer(), raw) }.getOrNull()
    return error?.takeIf { it.message.isNotEmpty() }
}

/** Merges a message.updated / snapshot info (and its parts, when it carries them) into the message it names. */
fun mergeMessageUpdate(prev: List<Message>, info: JsonObject, parts: List<JsonObject>?): List<Message> {
    val id = info.str("id") ?: return prev
    val index = prev.indexOfFirst { it.messageId == id }
    if (index == -1) return prev
    val existing = prev[index]
    val time = info.child("time")
    val createdAt = time.num("created")
    val completedAt = time.num("completed")
    val modelID = info.str("modelID")

    val snapshotParts = parts?.mapIndexedNotNull { i, part -> mapCommittedSnapshotPart(id, part, i) }
    val mergedParts = snapshotParts?.let { mergeCommittedSnapshotParts(existing.parts, it) }
    val turnError = readTurnError(info)
    val finish = info.str("finish")
    val role = if (info["role"] == null) existing.role else toMessageRole(info.str("role"))

    val updated = existing.copy(
        parts = mergedParts ?: existing.parts,
        createdAt = createdAt ?: existing.createdAt,
        completedAt = existing.completedAt ?: completedAt?.takeIf { it > 0 },
        modelID = modelID ?: existing.modelID,
        turnError = if (turnError != null && turnError.message != existing.turnError?.message) turnError else existing.turnError,
        finish = finish ?: existing.finish,
        steered = existing.steered || info["steered"].bool == true,
        role = role,
    )
    // Data classes compare by value, so "nothing new" falls out of equality (the TS lists each field instead).
    if (updated == existing) return prev
    return prev.toMutableList().apply { set(index, updated) }
}

internal fun mapCommittedSnapshotPart(messageId: String, part: JsonObject, index: Int): Part? {
    val id = part.str("id")
    return when (part.str("type")) {
        "text" -> TextPart(id ?: "$messageId-text-$index", part.str("text") ?: "")
        "reasoning" -> ReasoningPart(id ?: "$messageId-reasoning-$index", part.str("text") ?: "", part.str("summary"))
        "tool" -> ToolPart(id ?: "$messageId-tool-$index", part.str("tool") ?: "", part.str("callID") ?: "", part["state"].obj)
        "file" -> FilePart(id ?: "$messageId-file-$index", part.str("mime") ?: "", part.str("filename"), part.str("url") ?: "")
        else -> null
    }
}

/**
 * The snapshot's parts win, except streamed text that is already longer than the snapshot's, and parts only
 * known live are kept. Same order as the TS: text, files, reasoning, tools.
 */
internal fun mergeCommittedSnapshotParts(existingParts: List<Part>, snapshotParts: List<Part>): List<Part> {
    val existingText = existingParts.filterIsInstance<TextPart>().associateBy { it.partId }
    val mergedText = snapshotParts.filterIsInstance<TextPart>().map { snap ->
        val known = existingText[snap.partId]
        if (known != null && known.text.length > snap.text.length) known else snap
    }
    fun <T : Part> keep(type: (Part) -> Boolean): List<Part> {
        val snap = snapshotParts.filter(type)
        val ids = snap.map { it.partId }.toSet()
        return snap + existingParts.filter { type(it) && it.partId !in ids }
    }
    val files = keep<FilePart> { it is FilePart }
    val reasoning = keep<ReasoningPart> { it is ReasoningPart }
    val tools = keep<ToolPart> { it is ToolPart }
    return mergedText + files + reasoning + tools
}

/** A `message.part.updated`: adds or replaces the part, creating its message (as the agent's) if it isn't there yet. */
fun applyPartUpdate(prev: List<Message>, part: JsonObject): List<Message> {
    val messageId = part.str("messageID") ?: return prev
    val sessionId = part.str("sessionID") ?: ""
    var msgs = prev
    if (msgs.none { it.messageId == messageId }) {
        msgs = insertMessageSorted(prev, Message(messageId, sessionId, Role.Assistant))
    }
    val partId = part.str("id") ?: return msgs
    return msgs.map { msg ->
        if (msg.messageId != messageId) return@map msg
        val newPart: Part = when (part.str("type")) {
            "text" -> TextPart(partId, part.str("text") ?: "")
            "reasoning" -> ReasoningPart(partId, part.str("text") ?: "", part.str("summary"))
            "tool" -> {
                val existing = msg.parts.firstOrNull { it is ToolPart && it.partId == partId } as ToolPart?
                val incoming = part["state"].obj
                val state = if (existing != null) JsonObject((existing.state ?: emptyMap()) + (incoming ?: emptyMap())) else incoming
                ToolPart(partId, part.str("tool") ?: "", part.str("callID") ?: "", state)
            }
            "file" -> FilePart(partId, part.str("mime") ?: "", part.str("filename"), part.str("url") ?: "")
            else -> return@map msg // step-start / step-finish / compaction: not drawn on the phone
        }
        val at = msg.parts.indexOfFirst { it.partId == partId }
        if (at == -1) msg.copy(parts = msg.parts + newPart)
        else msg.copy(parts = msg.parts.toMutableList().apply { set(at, newPart) })
    }
}

/**
 * A streamed delta. With its `offset` (where it starts in the part's text) only text the part doesn't have yet is
 * added: a snapshot taken mid-reply already holds the text so far.
 */
fun applyTextDelta(prev: List<Message>, messageId: String, partId: String, sessionId: String, delta: String, offset: Long?): List<Message> {
    val msgIndex = prev.indexOfFirst { it.messageId == messageId }
    if (msgIndex == -1) {
        return insertMessageSorted(prev, Message(messageId, sessionId, Role.Assistant, listOf(TextPart(partId, delta))))
    }
    val msg = prev[msgIndex]
    val partIndex = msg.parts.indexOfFirst { (it is TextPart || it is ReasoningPart) && it.partId == partId }
    val updatedMsg = if (partIndex != -1) {
        val existing = msg.parts[partIndex]
        val existingText = if (existing is TextPart) existing.text else (existing as ReasoningPart).text
        val known = if (offset == null) 0 else maxOf(0, existingText.length - offset.toInt())
        if (known >= delta.length) return prev
        val appended = existingText + delta.substring(known)
        val newPart = if (existing is TextPart) existing.copy(text = appended) else (existing as ReasoningPart).copy(text = appended)
        msg.copy(parts = msg.parts.toMutableList().apply { set(partIndex, newPart) })
    } else {
        msg.copy(parts = msg.parts + TextPart(partId, delta))
    }
    return prev.toMutableList().apply { set(msgIndex, updatedMsg) }
}

internal fun JsonObject.partsList(): List<JsonObject>? = this["parts"].arr?.mapNotNull { it.obj }
