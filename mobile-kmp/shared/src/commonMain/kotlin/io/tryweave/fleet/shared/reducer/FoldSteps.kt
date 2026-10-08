package io.tryweave.fleet.shared.reducer

import io.tryweave.fleet.shared.model.Message
import io.tryweave.fleet.shared.model.Role
import io.tryweave.fleet.shared.model.TextPart
import io.tryweave.fleet.shared.model.ToolPart
import io.tryweave.fleet.shared.model.FilePart
import io.tryweave.fleet.shared.model.arr
import io.tryweave.fleet.shared.model.child
import io.tryweave.fleet.shared.model.obj
import io.tryweave.fleet.shared.model.str
import io.tryweave.fleet.shared.model.string
import io.tryweave.fleet.shared.model.text

/**
 * Port of client/src/lib/phone/fold-steps.ts (foldMessages + stepRow + summarizeSteps + visibleSteps) and the
 * phone's pendingQuestion (dock-state.ts). Diff line counts on edit rows and step details are skipped.
 */

sealed interface PhoneBlock {
    val key: String

    data class User(override val key: String, val text: String, val images: Int, val steered: Boolean) : PhoneBlock
    data class Text(override val key: String, val text: String) : PhoneBlock
    data class Steps(override val key: String, val steps: List<StepRow>, val summary: String, val running: Boolean, val failed: Int) : PhoneBlock
    data class Subagent(override val key: String, val title: String, val agent: String, val running: Boolean) : PhoneBlock
    data class Question(override val key: String, val question: String, val answer: String?, val pending: Boolean) : PhoneBlock
    data class Error(override val key: String, val text: String, val limit: Boolean) : PhoneBlock
}

enum class StepCategory { Read, Edit, Run, Search, Other }

enum class StepResult { Running, Failed, Done }

/** A tool call as the desktop's row draws it: "Read", the path, and how it ended. */
data class StepRow(val id: String, val label: String, val detail: String, val category: StepCategory, val result: StepResult, val pattern: Boolean)

private val SUBAGENT_TOOLS = setOf("task", "subagent")
private val READ_TOOLS = setOf("read", "list", "ls", "webfetch", "fetch")
private val EDIT_TOOLS = setOf("edit", "write", "patch", "multiedit", "apply_patch")
private val RUN_TOOLS = setOf("bash", "shell")
private val SEARCH_TOOLS = setOf("grep", "glob", "search", "codesearch", "websearch")
private val PATTERN_TOOLS = setOf("grep", "glob")

fun stepCategory(tool: String): StepCategory = when (tool.lowercase()) {
    in READ_TOOLS -> StepCategory.Read
    in EDIT_TOOLS -> StepCategory.Edit
    in RUN_TOOLS -> StepCategory.Run
    in SEARCH_TOOLS -> StepCategory.Search
    else -> StepCategory.Other
}

private fun titleCase(tool: String) = tool.replaceFirstChar { it.uppercaseChar() }

fun stepRow(part: ToolPart): StepRow {
    val input = part.input
    val name = part.tool.lowercase()
    val category = stepCategory(part.tool)
    val path = input.text("filePath") ?: input.str("path") ?: ""
    val pattern = name in PATTERN_TOOLS && input.text("pattern") != null
    val detail = when {
        category == StepCategory.Run && input.str("command") != null -> input.str("command")!!
        pattern -> input.str("pattern")!!
        path.isNotEmpty() -> path
        else -> input.text("description") ?: part.tool
    }
    val result = when (part.status) {
        "completed" -> StepResult.Done
        "error" -> StepResult.Failed
        else -> StepResult.Running
    }
    return StepRow(part.partId.ifEmpty { part.callId }, titleCase(part.tool), detail, category, result, pattern)
}

private fun plural(count: Int, one: String, many: String) = "$count ${if (count == 1) one else many}"

/** "Read 4 files · edited 1 · ran 1 command · searched 2", first word capitalised. */
fun summarizeSteps(steps: List<StepRow>): String {
    fun count(c: StepCategory) = steps.count { it.category == c }
    val parts = buildList {
        count(StepCategory.Read).takeIf { it > 0 }?.let { add("read ${plural(it, "file", "files")}") }
        count(StepCategory.Edit).takeIf { it > 0 }?.let { add("edited $it") }
        count(StepCategory.Run).takeIf { it > 0 }?.let { add("ran ${plural(it, "command", "commands")}") }
        count(StepCategory.Search).takeIf { it > 0 }?.let { add("searched $it") }
        count(StepCategory.Other).takeIf { it > 0 }?.let { add("used ${plural(it, "tool", "tools")}") }
    }
    return parts.joinToString(" · ").ifEmpty { "Worked" }.replaceFirstChar { it.uppercaseChar() }
}

/** The first three rows, and the rest as "N more steps" (never "1 more step": that one shows). */
fun <T> visibleSteps(steps: List<T>, shown: Int = 3): Pair<List<T>, List<T>> =
    if (steps.size <= shown + 1) steps to emptyList() else steps.take(shown) to steps.drop(shown)

private fun questionBlock(part: ToolPart): PhoneBlock.Question? {
    val first = part.input?.get("questions").arr?.firstOrNull().obj ?: return null
    val status = part.status
    val answers = part.state.child("metadata")?.get("answers").arr
    val answer = answers?.flatMap { it.arr.orEmpty() }?.mapNotNull { it.string }?.joinToString(", ")
        ?: part.state.str("output")
    return PhoneBlock.Question(
        key = "q:${part.partId.ifEmpty { part.callId }}",
        question = first.str("question") ?: "",
        answer = if (status == "completed") answer?.ifEmpty { null } ?: "answered" else null,
        pending = status == "pending" || status == "running",
    )
}

/** The conversation as phone blocks, oldest first. */
fun foldMessages(messages: List<Message>): List<PhoneBlock> {
    val blocks = mutableListOf<PhoneBlock>()
    var run = mutableListOf<StepRow>()
    fun flush() {
        if (run.isEmpty()) return
        blocks += PhoneBlock.Steps(
            key = "s:${run[0].id}",
            steps = run,
            summary = summarizeSteps(run),
            running = run.any { it.result == StepResult.Running },
            failed = run.count { it.result == StepResult.Failed },
        )
        run = mutableListOf()
    }

    for (message in messages) {
        if (message.role == Role.Shell) continue // shell commands (`!ls`) aren't sent from this phone
        if (message.role == Role.User) {
            flush()
            val text = message.parts.filterIsInstance<TextPart>().joinToString("\n") { it.text }.trim()
            val images = message.parts.count { it is FilePart }
            if (text.isNotEmpty() || images > 0) blocks += PhoneBlock.User("u:${message.messageId}", text, images, message.steered)
            continue
        }
        message.parts.forEachIndexed { index, part ->
            when (part) {
                is TextPart -> if (part.text.isNotBlank()) {
                    flush()
                    blocks += PhoneBlock.Text("t:${message.messageId}:$index", part.text)
                }
                is ToolPart -> when {
                    part.tool == "question" -> questionBlock(part)?.let { flush(); blocks += it }
                    part.tool.lowercase() in SUBAGENT_TOOLS -> {
                        flush()
                        val input = part.input
                        blocks += PhoneBlock.Subagent(
                            key = "a:${part.partId}",
                            title = input.text("description") ?: titleCase(part.tool),
                            agent = input.text("subagent_type") ?: input.text("agent") ?: "agent",
                            running = part.status == "pending" || part.status == "running",
                        )
                    }
                    else -> run += stepRow(part)
                }
                else -> Unit
            }
        }
        message.turnError?.let {
            flush()
            blocks += PhoneBlock.Error("e:${message.messageId}", it.message.ifEmpty { "The turn failed." }, it.kind != null)
        }
    }
    flush()
    return blocks
}

/** A question the agent is waiting on. Its request id is the tool call id. */
data class PendingQuestion(
    val requestId: String,
    val header: String,
    val question: String,
    val options: List<String>,
    val multiple: Boolean,
    val custom: Boolean,
    val more: Int,
)

/** The newest question tool call still waiting for an answer; a call answered anywhere counts as answered. */
fun pendingQuestion(messages: List<Message>): PendingQuestion? {
    val tools = messages.flatMap { m -> m.parts.filterIsInstance<ToolPart>() }.filter { it.tool == "question" }
    val settled = tools.filter { it.status == "completed" || it.status == "error" }.map { it.callId }.toSet()
    for (part in tools.asReversed()) {
        if (part.callId in settled || (part.status != "pending" && part.status != "running")) continue
        val questions = part.input?.get("questions").arr ?: continue
        val first = questions.firstOrNull().obj ?: continue
        return PendingQuestion(
            requestId = part.callId,
            header = first.str("header") ?: "Question",
            question = first.str("question") ?: "",
            options = first["options"].arr.orEmpty().mapNotNull { it.obj.str("label") },
            multiple = first["multiple"]?.let { it.toString() == "true" } ?: false,
            custom = first["custom"]?.let { it.toString() != "false" } ?: true,
            more = questions.size - 1,
        )
    }
    return null
}

/** One row in a tools box: a tool call, or a sub-agent. */
data class ToolRow(
    val id: String,
    val label: String,
    val detail: String,
    val category: StepCategory,
    val result: StepResult,
    val subagent: Boolean,
)

/** What the conversation draws: blocks, with runs of tool calls and sub-agents next to each other in one box. */
sealed interface PhoneItem {
    val key: String

    data class Block(val block: PhoneBlock) : PhoneItem {
        override val key: String get() = block.key
    }

    data class Tools(override val key: String, val rows: List<ToolRow>) : PhoneItem {
        /** The first three rows and how many more there are (never "1 more"). */
        val shown: List<ToolRow> get() = visibleSteps(rows).first
        val hidden: Int get() = visibleSteps(rows).second.size
    }
}

/** Port of groupTools (fold-steps.ts), flattened into rows so both UIs draw one kind of row. */
fun groupTools(blocks: List<PhoneBlock>): List<PhoneItem> {
    val items = mutableListOf<PhoneItem>()
    for (block in blocks) {
        val rows = when (block) {
            is PhoneBlock.Steps -> block.steps.map { ToolRow(it.id, it.label, it.detail, it.category, it.result, false) }
            is PhoneBlock.Subagent -> listOf(
                ToolRow(block.key, block.agent.replaceFirstChar { it.uppercaseChar() }, block.title, StepCategory.Other, if (block.running) StepResult.Running else StepResult.Done, true),
            )
            else -> null
        }
        if (rows == null) {
            items += PhoneItem.Block(block)
            continue
        }
        val last = items.lastOrNull()
        if (last is PhoneItem.Tools) items[items.lastIndex] = last.copy(rows = last.rows + rows)
        else items += PhoneItem.Tools("g:${block.key}", rows)
    }
    return items
}
