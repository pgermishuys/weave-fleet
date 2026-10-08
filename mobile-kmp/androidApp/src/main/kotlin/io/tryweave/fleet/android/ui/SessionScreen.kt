package io.tryweave.fleet.android.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.imePadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.BasicText
import androidx.compose.foundation.text.BasicTextField
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.SolidColor
import androidx.compose.ui.platform.testTag
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.compose.LifecycleEventEffect
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import io.tryweave.fleet.shared.model.PermissionAsk
import io.tryweave.fleet.shared.model.PermissionReply
import io.tryweave.fleet.shared.reducer.PendingQuestion
import io.tryweave.fleet.shared.reducer.PhoneBlock
import io.tryweave.fleet.shared.reducer.StepCategory
import io.tryweave.fleet.shared.reducer.StepResult
import io.tryweave.fleet.shared.reducer.PhoneItem
import io.tryweave.fleet.shared.reducer.ToolRow
import io.tryweave.fleet.shared.reducer.StreamStatus
import io.tryweave.fleet.shared.signalr.HubState
import io.tryweave.fleet.shared.store.FleetClient
import io.tryweave.fleet.shared.store.SessionController
import io.tryweave.fleet.shared.store.SessionUiState

@Composable
fun SessionScreen(client: FleetClient, sessionId: String, onBack: () -> Unit) {
    val controller = remember(sessionId) { client.openSession(sessionId) }
    DisposableEffect(controller) { onDispose { controller.close() } }
    // In the background the phone isn't looking: Fleet may notify about this session then.
    LifecycleEventEffect(Lifecycle.Event.ON_START) { controller.setFocused(true) }
    LifecycleEventEffect(Lifecycle.Event.ON_STOP) { controller.setFocused(false) }
    val state by controller.state.collectAsState()
    val now = rememberNow()

    Column(Modifier.fillMaxSize().imePadding().padding(horizontal = 8.dp, vertical = 6.dp).clip(RoundedCornerShape(Fleet.radiusPanel)).background(Fleet.panel)) {
        Header(state, now, onBack)
        Box(Modifier.fillMaxWidth().height(1.dp).background(Fleet.border))
        val items = remember(state.items) { state.items.asReversed() }
        LazyColumn(
            Modifier.weight(1f).fillMaxWidth(),
            reverseLayout = true,
            verticalArrangement = Arrangement.spacedBy(14.dp, Alignment.Bottom),
            contentPadding = androidx.compose.foundation.layout.PaddingValues(horizontal = 16.dp, vertical = 16.dp),
        ) {
            if (state.isWorking && !state.needsYou) item(key = "working") { WorkingLine(state, now) }
            items(items, key = { it.key }) { item ->
                when (item) {
                    is PhoneItem.Tools -> ToolsBox(item)
                    is PhoneItem.Block -> BlockView(item.block)
                }
            }
            if (state.loading) item { BasicText("Loading…", style = Fleet.small) }
        }
        state.error?.let { BasicText(it, style = Fleet.small.copy(color = Fleet.error), modifier = Modifier.padding(horizontal = 16.dp, vertical = 4.dp)) }
        Dock(controller, state)
    }
}

@Composable
private fun Header(state: SessionUiState, now: Long, onBack: () -> Unit) {
    Row(Modifier.fillMaxWidth().heightIn(min = 64.dp).padding(horizontal = 8.dp, vertical = 8.dp), verticalAlignment = Alignment.CenterVertically) {
        Box(Modifier.size(44.dp).clip(CircleShape).clickable(onClick = onBack), contentAlignment = Alignment.Center) { ChevronLeft() }
        Spacer(Modifier.width(4.dp))
        Column(Modifier.weight(1f)) {
            BasicText(state.title, style = Fleet.title, maxLines = 1, overflow = TextOverflow.Ellipsis)
            Row(verticalAlignment = Alignment.CenterVertically, modifier = Modifier.padding(top = 2.dp)) {
                val status = when {
                    state.needsYou -> "Needs you"
                    state.status == StreamStatus.Retry -> "Retrying"
                    state.isWorking -> "Working"
                    else -> "Idle"
                }
                when {
                    state.needsYou -> Dot(Fleet.waiting, 7.dp)
                    state.isWorking -> TickingDots(Fleet.muted, 10.dp)
                }
                if (state.needsYou || state.isWorking) Spacer(Modifier.width(6.dp))
                val parts = listOfNotNull(
                    status,
                    if (state.isWorking) elapsed(state.workingSinceMs, now) else null,
                    state.folder.takeIf { it.isNotEmpty() },
                    if (state.connection != HubState.Connected) "reconnecting…" else null,
                )
                BasicText(parts.joinToString(" · "), style = Fleet.small.copy(fontSize = Fleet.body.fontSize.times(0.93f)), maxLines = 1)
            }
        }
    }
}

@Composable
private fun WorkingLine(state: SessionUiState, now: Long) {
    Row(verticalAlignment = Alignment.CenterVertically, modifier = Modifier.padding(start = 2.dp, top = 2.dp)) {
        TickingDots(Fleet.text, 11.dp)
        Spacer(Modifier.width(10.dp))
        BasicText("Working · ${elapsed(state.workingSinceMs, now)}", style = Fleet.body.copy(color = Fleet.muted))
    }
}

@Composable
private fun BlockView(block: PhoneBlock) {
    when (block) {
        is PhoneBlock.User -> Row(Modifier.fillMaxWidth()) {
            Spacer(Modifier.weight(1f).widthIn(min = 48.dp))
            val shape = RoundedCornerShape(14.dp)
            BasicText(
                block.text + if (block.images > 0) "\n[${block.images} image${if (block.images == 1) "" else "s"}]" else "",
                style = Fleet.body,
                modifier = Modifier.widthIn(max = 300.dp).background(Fleet.card, shape).border(1.dp, Fleet.border, shape).padding(horizontal = 14.dp, vertical = 10.dp),
            )
        }
        is PhoneBlock.Text -> Markdown(block.text, Modifier.fillMaxWidth().padding(horizontal = 2.dp))
        is PhoneBlock.Question -> Row(verticalAlignment = Alignment.Top) {
            QuestionIcon(if (block.pending) Fleet.waiting else Fleet.muted, 18.dp)
            Spacer(Modifier.width(10.dp))
            Column {
                BasicText(block.question, style = Fleet.body.copy(color = if (block.pending) Fleet.text else Fleet.muted))
                BasicText(block.answer?.let { "You answered: $it" } ?: "Waiting for your answer", style = Fleet.small.copy(color = if (block.pending) Fleet.waiting else Fleet.muted))
            }
        }
        is PhoneBlock.Error -> Card(Modifier.fillMaxWidth(), borderColor = Fleet.error.copy(alpha = 0.45f), background = Fleet.error.copy(alpha = 0.07f)) {
            Column(Modifier.padding(14.dp)) {
                BasicText(if (block.limit) "Hit a limit" else "The turn failed", style = Fleet.label.copy(color = Fleet.error, fontWeight = FontWeight.SemiBold))
                BasicText(block.text, style = Fleet.body.copy(color = Fleet.text), modifier = Modifier.padding(top = 4.dp))
            }
        }
        is PhoneBlock.Steps, is PhoneBlock.Subagent -> Unit // grouped into PhoneItem.Tools
    }
}

@Composable
private fun ToolsBox(box: PhoneItem.Tools) {
    var expanded by remember { mutableStateOf(false) }
    Card(Modifier.fillMaxWidth()) {
        Column(Modifier.padding(vertical = 4.dp)) {
            (if (expanded) box.rows else box.shown).forEach { ToolRowView(it) }
            if (box.hidden > 0 && !expanded) {
                Row(Modifier.fillMaxWidth().height(Fleet.row).clickable { expanded = true }.padding(horizontal = 14.dp), verticalAlignment = Alignment.CenterVertically) {
                    BasicText("${box.hidden} more steps", style = Fleet.body.copy(color = Fleet.muted))
                }
            }
        }
    }
}

@Composable
private fun ToolRowView(row: ToolRow) {
    Row(Modifier.fillMaxWidth().height(Fleet.row).padding(horizontal = 14.dp), verticalAlignment = Alignment.CenterVertically) {
        when {
            row.subagent -> if (row.result == StepResult.Running) TickingDots(Fleet.muted, 14.dp) else Check(Fleet.muted)
            row.category == StepCategory.Run -> TerminalIcon()
            row.category == StepCategory.Search -> SearchIcon()
            row.category == StepCategory.Edit -> PencilIcon()
            else -> FileIcon()
        }
        Spacer(Modifier.width(12.dp))
        BasicText(row.label, style = Fleet.label.copy(fontWeight = FontWeight.SemiBold))
        Spacer(Modifier.width(10.dp))
        BasicText(
            row.detail,
            style = if (row.subagent) Fleet.body.copy(color = Fleet.muted) else Fleet.code,
            maxLines = 1,
            overflow = TextOverflow.Ellipsis,
            modifier = Modifier.weight(1f),
        )
        if (!row.subagent) {
            Spacer(Modifier.width(10.dp))
            when (row.result) {
                StepResult.Done -> Check()
                StepResult.Running -> Dot(Fleet.running, 8.dp)
                StepResult.Failed -> Cross()
            }
        }
    }
}

// ── The bottom: whatever needs you, else the composer ────────────────────────────────────────────────────────

@Composable
private fun Dock(controller: SessionController, state: SessionUiState) {
    Box(Modifier.fillMaxWidth().padding(10.dp)) {
        val ask = state.permission
        val question = state.question
        when {
            ask != null -> PermissionDock(ask) { controller.reply(ask, it) }
            question != null -> QuestionDock(question) { controller.answer(question, it) }
            else -> Composer(state, onSend = controller::send)
        }
    }
}

@Composable
private fun PermissionDock(ask: PermissionAsk, onReply: (PermissionReply) -> Unit) {
    Card(Modifier.fillMaxWidth(), borderColor = Fleet.waiting.copy(alpha = 0.55f), background = Fleet.card) {
        Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                ShieldIcon()
                Spacer(Modifier.width(12.dp))
                BasicText(ask.heading, style = Fleet.label.copy(fontWeight = FontWeight.SemiBold))
            }
            val what = ask.title ?: ask.tool
            CommandBox(listOfNotNull(ask.directory, if (ask.kind == "shell") "$ $what" else what).joinToString("\n"))
            Row(horizontalArrangement = Arrangement.spacedBy(10.dp)) {
                FleetButton("Allow once", Modifier.weight(1f), primary = true) { onReply(PermissionReply.Once) }
                FleetButton("Deny", Modifier.weight(1f)) { onReply(PermissionReply.Reject) }
            }
            BasicText("The agent waits until you answer.", style = Fleet.small)
        }
    }
}

@Composable
private fun QuestionDock(question: PendingQuestion, onAnswer: (String) -> Unit) {
    var typed by remember(question.requestId) { mutableStateOf("") }
    Card(Modifier.fillMaxWidth(), borderColor = Fleet.waiting.copy(alpha = 0.55f), background = Fleet.card) {
        Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(10.dp)) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                QuestionIcon()
                Spacer(Modifier.width(12.dp))
                BasicText(question.header, style = Fleet.label.copy(fontWeight = FontWeight.SemiBold))
            }
            BasicText(question.question, style = Fleet.body)
            question.options.forEachIndexed { i, option ->
                val shape = RoundedCornerShape(Fleet.radiusCard)
                Row(
                    Modifier.fillMaxWidth().heightIn(min = Fleet.row).clip(shape).background(Fleet.bg).border(1.dp, Fleet.border, shape).clickable { onAnswer(option) }.padding(horizontal = 12.dp),
                    verticalAlignment = Alignment.CenterVertically,
                ) {
                    val badge = RoundedCornerShape(6.dp)
                    Box(Modifier.size(24.dp).clip(badge).background(if (i == 0) Fleet.accent else Fleet.card).border(1.dp, Fleet.border, badge), contentAlignment = Alignment.Center) {
                        BasicText("${i + 1}", style = Fleet.small.copy(color = Fleet.text))
                    }
                    Spacer(Modifier.width(12.dp))
                    BasicText(option, style = Fleet.label)
                }
            }
            if (question.custom) InputRow(typed, "Or type an answer…", enabled = true, tag = "answer", onChange = { typed = it }) { onAnswer(typed.trim()); typed = "" }
        }
    }
}

@Composable
private fun Composer(state: SessionUiState, onSend: (String) -> Unit) {
    var draft by remember { mutableStateOf("") }
    InputRow(draft, "Message ${state.folder.ifEmpty { "the agent" }}…", enabled = !state.sending, tag = "composer", onChange = { draft = it }) {
        onSend(draft)
        draft = ""
    }
}

@Composable
private fun InputRow(value: String, placeholder: String, enabled: Boolean, tag: String, onChange: (String) -> Unit, onSend: () -> Unit) {
    val shape = RoundedCornerShape(Fleet.radiusPanel)
    Row(
        Modifier.fillMaxWidth().background(Fleet.card, shape).border(1.dp, Fleet.borderStrong, shape).padding(start = 16.dp, end = 8.dp, top = 8.dp, bottom = 8.dp),
        verticalAlignment = Alignment.Bottom,
    ) {
        BasicTextField(
            value = value,
            onValueChange = onChange,
            textStyle = Fleet.body,
            cursorBrush = SolidColor(Fleet.accent),
            maxLines = 6,
            modifier = Modifier.weight(1f).testTag(tag).padding(vertical = 9.dp),
            decorationBox = { inner ->
                if (value.isEmpty()) BasicText(placeholder, style = Fleet.body.copy(color = Fleet.faint), maxLines = 1)
                inner()
            },
        )
        Spacer(Modifier.width(8.dp))
        val ready = enabled && value.isNotBlank()
        Box(
            Modifier.size(40.dp).testTag(if (tag == "composer") "send" else "$tag-send").clip(CircleShape).background(if (ready) Fleet.accent else Fleet.borderStrong).clickable(enabled = ready, onClick = onSend),
            contentAlignment = Alignment.Center,
        ) { ArrowUp(if (ready) androidx.compose.ui.graphics.Color.White else Fleet.muted) }
    }
}
