package io.tryweave.fleet.android.ui

import androidx.compose.animation.core.LinearEasing
import androidx.compose.animation.core.RepeatMode
import androidx.compose.animation.core.animateFloat
import androidx.compose.animation.core.infiniteRepeatable
import androidx.compose.animation.core.rememberInfiniteTransition
import androidx.compose.animation.core.tween
import androidx.compose.foundation.Canvas
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.interaction.MutableInteractionSource
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.RowScope
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.BasicText
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.remember
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.Path
import androidx.compose.ui.graphics.StrokeCap
import androidx.compose.ui.graphics.StrokeJoin
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import io.tryweave.fleet.shared.reducer.StreamStatus

/** Fleet's "Quad" status: four dots in a square, one dimming in turn while the agent works. */
@Composable
fun TickingDots(color: Color = Fleet.text, size: Dp = 12.dp, ticking: Boolean = true) {
    val transition = rememberInfiniteTransition(label = "dots")
    val phase by transition.animateFloat(0f, 4f, infiniteRepeatable(tween(1200, easing = LinearEasing), RepeatMode.Restart), label = "phase")
    Canvas(Modifier.size(size)) {
        val r = this.size.minDimension * 0.17f
        val offsets = listOf(Offset(r * 1.3f, r * 1.3f), Offset(this.size.width - r * 1.3f, r * 1.3f), Offset(this.size.width - r * 1.3f, this.size.height - r * 1.3f), Offset(r * 1.3f, this.size.height - r * 1.3f))
        offsets.forEachIndexed { i, o ->
            val dim = ticking && phase.toInt() == i
            drawCircle(color.copy(alpha = if (dim) 0.25f else 1f), r, o)
        }
    }
}

@Composable
fun Dot(color: Color, size: Dp = 8.dp) {
    Box(Modifier.size(size).clip(CircleShape).background(color))
}

/** A session's status mark: ticking dots while working, an amber dot when it needs you, nothing when idle. */
@Composable
fun StatusMark(status: String?, size: Dp = 12.dp) {
    when (status) {
        "busy", "working", "retry", "delegating" -> TickingDots(Fleet.text, size)
        "waiting_input" -> Box(Modifier.size(size), contentAlignment = Alignment.Center) { Dot(Fleet.waiting, 8.dp) }
        else -> Box(Modifier.size(size))
    }
}

fun StreamStatus.wire(): String = when (this) {
    StreamStatus.Idle -> "idle"
    StreamStatus.Busy -> "busy"
    StreamStatus.Retry -> "retry"
    StreamStatus.Delegating -> "delegating"
    StreamStatus.WaitingInput -> "waiting_input"
}

@Composable
fun FleetButton(
    text: String,
    modifier: Modifier = Modifier,
    primary: Boolean = false,
    enabled: Boolean = true,
    onClick: () -> Unit,
) {
    val shape = RoundedCornerShape(Fleet.radiusButton)
    Box(
        modifier
            .height(Fleet.row)
            .clip(shape)
            .background(if (primary) Fleet.accent.copy(alpha = if (enabled) 1f else 0.5f) else Fleet.bg)
            .border(1.dp, if (primary) Color.Transparent else Fleet.borderStrong, shape)
            .clickable(enabled = enabled, onClick = onClick)
            .padding(horizontal = 16.dp),
        contentAlignment = Alignment.Center,
    ) {
        BasicText(text, style = Fleet.label.copy(color = if (primary) Color.White else Fleet.text, fontWeight = FontWeight.SemiBold, textAlign = TextAlign.Center), maxLines = 1)
    }
}

@Composable
fun Card(modifier: Modifier = Modifier, borderColor: Color = Fleet.border, background: Color = Fleet.card, content: @Composable () -> Unit) {
    val shape = RoundedCornerShape(Fleet.radiusCard)
    Box(modifier.clip(shape).background(background).border(1.dp, borderColor, shape)) { content() }
}

@Composable
fun Pressable(modifier: Modifier = Modifier, onClick: () -> Unit, content: @Composable RowScope.() -> Unit) {
    Row(
        modifier.clickable(interactionSource = remember { MutableInteractionSource() }, indication = null, onClick = onClick),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.Start,
        content = content,
    )
}

// ── Icons, drawn: no icon font and no Material icons ─────────────────────────────────────────────────────────

@Composable
fun ChevronLeft(color: Color = Fleet.text, size: Dp = 22.dp) = Canvas(Modifier.size(size)) {
    val p = Path().apply { moveTo(this@Canvas.size.width * 0.62f, this@Canvas.size.height * 0.2f); lineTo(this@Canvas.size.width * 0.32f, this@Canvas.size.height * 0.5f); lineTo(this@Canvas.size.width * 0.62f, this@Canvas.size.height * 0.8f) }
    drawPath(p, color, style = Stroke(width = 2.dp.toPx(), cap = StrokeCap.Round, join = StrokeJoin.Round))
}

@Composable
fun ChevronRight(color: Color = Fleet.muted, size: Dp = 16.dp) = Canvas(Modifier.size(size)) {
    val w = this.size.width
    val h = this.size.height
    val p = Path().apply { moveTo(w * 0.38f, h * 0.2f); lineTo(w * 0.68f, h * 0.5f); lineTo(w * 0.38f, h * 0.8f) }
    drawPath(p, color, style = Stroke(width = 1.8.dp.toPx(), cap = StrokeCap.Round, join = StrokeJoin.Round))
}

@Composable
fun Check(color: Color = Fleet.running, size: Dp = 16.dp) = Canvas(Modifier.size(size)) {
    val w = this.size.width
    val h = this.size.height
    val p = Path().apply { moveTo(w * 0.15f, h * 0.52f); lineTo(w * 0.4f, h * 0.76f); lineTo(w * 0.86f, h * 0.26f) }
    drawPath(p, color, style = Stroke(width = 1.8.dp.toPx(), cap = StrokeCap.Round, join = StrokeJoin.Round))
}

@Composable
fun Cross(color: Color = Fleet.error, size: Dp = 14.dp) = Canvas(Modifier.size(size)) {
    val w = this.size.width
    val stroke = Stroke(width = 1.8.dp.toPx(), cap = StrokeCap.Round)
    drawLine(color, Offset(w * 0.2f, w * 0.2f), Offset(w * 0.8f, w * 0.8f), stroke.width, StrokeCap.Round)
    drawLine(color, Offset(w * 0.8f, w * 0.2f), Offset(w * 0.2f, w * 0.8f), stroke.width, StrokeCap.Round)
}

@Composable
fun FileIcon(color: Color = Fleet.muted, size: Dp = 18.dp) = Canvas(Modifier.size(size)) {
    val w = this.size.width
    val h = this.size.height
    val stroke = Stroke(width = 1.5.dp.toPx(), cap = StrokeCap.Round, join = StrokeJoin.Round)
    val p = Path().apply {
        moveTo(w * 0.22f, h * 0.1f); lineTo(w * 0.6f, h * 0.1f); lineTo(w * 0.8f, h * 0.3f); lineTo(w * 0.8f, h * 0.9f)
        lineTo(w * 0.22f, h * 0.9f); close()
    }
    drawPath(p, color, style = stroke)
    drawLine(color, Offset(w * 0.35f, h * 0.5f), Offset(w * 0.66f, h * 0.5f), stroke.width)
    drawLine(color, Offset(w * 0.35f, h * 0.66f), Offset(w * 0.66f, h * 0.66f), stroke.width)
}

@Composable
fun TerminalIcon(color: Color = Fleet.muted, size: Dp = 18.dp) = Canvas(Modifier.size(size)) {
    val w = this.size.width
    val h = this.size.height
    val stroke = Stroke(width = 1.6.dp.toPx(), cap = StrokeCap.Round, join = StrokeJoin.Round)
    val p = Path().apply { moveTo(w * 0.15f, h * 0.3f); lineTo(w * 0.4f, h * 0.5f); lineTo(w * 0.15f, h * 0.7f) }
    drawPath(p, color, style = stroke)
    drawLine(color, Offset(w * 0.5f, h * 0.75f), Offset(w * 0.85f, h * 0.75f), stroke.width, StrokeCap.Round)
}

@Composable
fun SearchIcon(color: Color = Fleet.muted, size: Dp = 18.dp) = Canvas(Modifier.size(size)) {
    val w = this.size.width
    val stroke = Stroke(width = 1.6.dp.toPx(), cap = StrokeCap.Round)
    drawCircle(color, w * 0.28f, Offset(w * 0.44f, w * 0.44f), style = stroke)
    drawLine(color, Offset(w * 0.65f, w * 0.65f), Offset(w * 0.85f, w * 0.85f), stroke.width, StrokeCap.Round)
}

@Composable
fun PencilIcon(color: Color = Fleet.muted, size: Dp = 18.dp) = Canvas(Modifier.size(size)) {
    val w = this.size.width
    val stroke = Stroke(width = 1.6.dp.toPx(), cap = StrokeCap.Round, join = StrokeJoin.Round)
    val p = Path().apply { moveTo(w * 0.2f, w * 0.8f); lineTo(w * 0.25f, w * 0.6f); lineTo(w * 0.65f, w * 0.2f); lineTo(w * 0.8f, w * 0.35f); lineTo(w * 0.4f, w * 0.75f); close() }
    drawPath(p, color, style = stroke)
}

@Composable
fun ShieldIcon(color: Color = Fleet.waiting, size: Dp = 20.dp) = Canvas(Modifier.size(size)) {
    val w = this.size.width
    val h = this.size.height
    val stroke = Stroke(width = 1.7.dp.toPx(), cap = StrokeCap.Round, join = StrokeJoin.Round)
    val p = Path().apply {
        moveTo(w * 0.5f, h * 0.08f); lineTo(w * 0.85f, h * 0.22f); lineTo(w * 0.85f, h * 0.5f)
        quadraticTo(w * 0.85f, h * 0.8f, w * 0.5f, h * 0.93f); quadraticTo(w * 0.15f, h * 0.8f, w * 0.15f, h * 0.5f)
        lineTo(w * 0.15f, h * 0.22f); close()
    }
    drawPath(p, color, style = stroke)
    drawLine(color, Offset(w * 0.5f, h * 0.32f), Offset(w * 0.5f, h * 0.56f), stroke.width, StrokeCap.Round)
    drawCircle(color, 1.1.dp.toPx(), Offset(w * 0.5f, h * 0.7f))
}

@Composable
fun QuestionIcon(color: Color = Fleet.waiting, size: Dp = 20.dp) = Box(Modifier.size(size).border(1.7.dp, color, CircleShape), contentAlignment = Alignment.Center) {
    BasicText("?", style = Fleet.small.copy(color = color, fontWeight = FontWeight.Bold))
}

@Composable
fun ArrowUp(color: Color = Fleet.text, size: Dp = 18.dp) = Canvas(Modifier.size(size)) {
    val w = this.size.width
    val stroke = Stroke(width = 2.dp.toPx(), cap = StrokeCap.Round, join = StrokeJoin.Round)
    drawLine(color, Offset(w * 0.5f, w * 0.85f), Offset(w * 0.5f, w * 0.15f), stroke.width, StrokeCap.Round)
    val p = Path().apply { moveTo(w * 0.2f, w * 0.45f); lineTo(w * 0.5f, w * 0.15f); lineTo(w * 0.8f, w * 0.45f) }
    drawPath(p, color, style = stroke)
}
