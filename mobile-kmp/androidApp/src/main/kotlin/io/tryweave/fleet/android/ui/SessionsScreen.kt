package io.tryweave.fleet.android.ui

import androidx.compose.foundation.Image
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
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.BasicText
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.produceState
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import io.tryweave.fleet.android.R
import io.tryweave.fleet.shared.model.SessionListItem
import io.tryweave.fleet.shared.signalr.HubState
import io.tryweave.fleet.shared.store.FleetClient
import io.tryweave.fleet.shared.store.NeedsYou
import kotlinx.coroutines.delay

@Composable
fun rememberNow(periodMs: Long = 1000): Long {
    val now by produceState(System.currentTimeMillis()) {
        while (true) { delay(periodMs); value = System.currentTimeMillis() }
    }
    return now
}

/** "42s", "6m 2s", "3h 4m", "13d 6h". */
fun elapsed(fromMs: Long?, now: Long): String {
    if (fromMs == null || fromMs <= 0) return ""
    val s = ((now - fromMs) / 1000).coerceAtLeast(0)
    return when {
        s < 60 -> "${s}s"
        s < 3600 -> "${s / 60}m ${s % 60}s"
        s < 86_400 -> "${s / 3600}h ${(s % 3600) / 60}m"
        else -> "${s / 86_400}d ${(s % 86_400) / 3600}h"
    }
}

@Composable
fun SessionsScreen(client: FleetClient, onOpen: (String) -> Unit, onForget: () -> Unit) {
    val state by client.sessionsState.collectAsState()
    val now = rememberNow()
    LaunchedEffect(Unit) { client.refreshSessions() }

    Column(Modifier.fillMaxSize()) {
        Row(Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 12.dp), verticalAlignment = Alignment.CenterVertically) {
            Image(painterResource(R.drawable.ic_mark), contentDescription = "Fleet", modifier = Modifier.size(30.dp))
            Spacer(Modifier.weight(1f))
            val shape = RoundedCornerShape(Fleet.radiusButton)
            Row(
                Modifier.clip(shape).border(1.dp, Fleet.borderStrong, shape).clickable(onClick = onForget).padding(horizontal = 12.dp, vertical = 8.dp),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                Dot(if (state.connection == HubState.Connected) Fleet.running else Fleet.waiting, 7.dp)
                Spacer(Modifier.width(8.dp))
                BasicText(state.machineName, style = Fleet.label)
            }
        }

        LazyColumn(
            Modifier.fillMaxSize().padding(horizontal = 8.dp).clip(RoundedCornerShape(Fleet.radiusPanel)).background(Fleet.panel),
            verticalArrangement = Arrangement.spacedBy(0.dp),
        ) {
            item {
                Column(Modifier.padding(start = 18.dp, end = 18.dp, top = 20.dp, bottom = 12.dp)) {
                    BasicText(if (state.needsYou.isNotEmpty()) "Needs you" else "Sessions", style = Fleet.display)
                    Row(Modifier.padding(top = 6.dp), verticalAlignment = Alignment.CenterVertically) {
                        Dot(if (state.connection == HubState.Connected) Fleet.running else Fleet.waiting, 7.dp)
                        Spacer(Modifier.width(8.dp))
                        BasicText(
                            "${state.machineName} · ${when (state.connection) { HubState.Connected -> "online"; HubState.Disconnected -> "offline"; else -> "connecting…" }}",
                            style = Fleet.small.copy(fontSize = Fleet.body.fontSize),
                        )
                    }
                    state.error?.let { BasicText(it, style = Fleet.small.copy(color = Fleet.error), modifier = Modifier.padding(top = 6.dp)) }
                }
            }
            items(state.needsYou, key = { "n:" + it.id }) { item ->
                NeedsYouCard(client, item, state.details[item.id], now, onOpen)
            }
            if (state.working.isNotEmpty()) {
                item { SectionLabel("Working", state.working.size) }
                items(state.working, key = { "w:" + it.id }) { SessionRow(it, now, onOpen) }
            }
            if (state.other.isNotEmpty()) {
                item { SectionLabel("Recent", state.other.size) }
                items(state.other, key = { "o:" + it.id }) { SessionRow(it, now, onOpen) }
            }
            if (!state.loading && state.needsYou.isEmpty() && state.working.isEmpty() && state.other.isEmpty()) {
                item { BasicText("No sessions on this machine yet.", style = Fleet.body.copy(color = Fleet.muted), modifier = Modifier.padding(18.dp)) }
            }
            item { Spacer(Modifier.size(24.dp)) }
        }
    }
}

@Composable
private fun SectionLabel(text: String, count: Int) {
    Row(Modifier.padding(start = 18.dp, end = 18.dp, top = 18.dp, bottom = 4.dp)) {
        BasicText(text, style = Fleet.small.copy(fontWeight = FontWeight.Medium, fontSize = Fleet.body.fontSize))
        Spacer(Modifier.width(8.dp))
        BasicText("$count", style = Fleet.small.copy(color = Fleet.faint, fontSize = Fleet.body.fontSize))
    }
}

@Composable
private fun SessionRow(item: SessionListItem, now: Long, onOpen: (String) -> Unit) {
    Row(
        Modifier.fillMaxWidth().heightIn(min = 60.dp).clickable { onOpen(item.id) }.padding(horizontal = 18.dp, vertical = 10.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Box(Modifier.width(26.dp)) { StatusMark(item.activityStatus) }
        Column(Modifier.weight(1f)) {
            BasicText(item.title, style = Fleet.label.copy(fontSize = Fleet.title.fontSize), maxLines = 1, overflow = TextOverflow.Ellipsis)
            val sub = listOfNotNull(item.folder.takeIf { it.isNotEmpty() }, item.branch).joinToString(" · ")
            if (sub.isNotEmpty()) BasicText(sub, style = Fleet.small.copy(fontSize = 14.sp), maxLines = 1, overflow = TextOverflow.Ellipsis)
        }
        Spacer(Modifier.width(10.dp))
        BasicText(elapsed(item.session.time?.updated ?: item.session.time?.created, now), style = Fleet.small.copy(fontSize = 14.sp))
    }
}


@Composable
private fun NeedsYouCard(client: FleetClient, item: SessionListItem, detail: NeedsYou?, now: Long, onOpen: (String) -> Unit) {
    val ask = detail?.permission
    val question = detail?.question
    Card(
        Modifier.fillMaxWidth().padding(horizontal = 12.dp, vertical = 6.dp),
        borderColor = Fleet.waiting.copy(alpha = 0.55f),
        background = Fleet.waiting.copy(alpha = 0.06f),
    ) {
        Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                if (question != null || ask == null) QuestionIcon() else ShieldIcon()
                Spacer(Modifier.width(12.dp))
                BasicText(ask?.heading ?: if (question != null) "Question" else "Needs you", style = Fleet.label.copy(fontWeight = FontWeight.SemiBold), modifier = Modifier.weight(1f))
                BasicText(elapsed(item.session.time?.updated, now).substringBefore(' '), style = Fleet.small.copy(fontSize = 14.sp))
            }
            Pressable(onClick = { onOpen(item.id) }) {
                BasicText(item.title, style = Fleet.label.copy(fontSize = 16.sp), modifier = Modifier.weight(1f), maxLines = 1, overflow = TextOverflow.Ellipsis)
                ChevronRight()
            }
            when {
                ask != null -> {
                    ask.title?.let { CommandBox(if (ask.kind == "shell") "$ $it" else it) }
                    Row(horizontalArrangement = Arrangement.spacedBy(10.dp)) {
                        FleetButton("Allow once", Modifier.weight(1f), primary = true) { client.replyToPermission(item.id, ask.id, true) { client.refreshSessions() } }
                        FleetButton("Deny", Modifier.weight(1f)) { client.replyToPermission(item.id, ask.id, false) { client.refreshSessions() } }
                    }
                }
                question != null -> {
                    BasicText(question.question, style = Fleet.body)
                    Row(horizontalArrangement = Arrangement.spacedBy(10.dp)) {
                        question.options.take(2).forEach { option ->
                            FleetButton(option, Modifier.weight(1f)) { client.answerQuestion(item.id, question.requestId, option) { client.refreshSessions() } }
                        }
                        FleetButton("More…", Modifier.weight(1f)) { onOpen(item.id) }
                    }
                }
                else -> FleetButton("Open", Modifier.fillMaxWidth()) { onOpen(item.id) }
            }
        }
    }
}

@Composable
fun CommandBox(text: String) {
    val shape = RoundedCornerShape(Fleet.radiusButton)
    BasicText(
        text,
        style = Fleet.code.copy(color = Fleet.text),
        maxLines = 3,
        overflow = TextOverflow.Ellipsis,
        modifier = Modifier.fillMaxWidth().background(Fleet.bg, shape).border(1.dp, Fleet.border, shape).padding(horizontal = 14.dp, vertical = 12.dp),
    )
}
