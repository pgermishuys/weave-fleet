package io.tryweave.fleet.android.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.BasicText
import androidx.compose.runtime.Composable
import androidx.compose.runtime.remember
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.AnnotatedString
import androidx.compose.ui.text.SpanStyle
import androidx.compose.ui.text.buildAnnotatedString
import androidx.compose.ui.text.font.FontStyle
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.withStyle
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp

/**
 * "Markdown-ish": paragraphs, #-headings, - and 1. lists, fenced code, **bold**, *italic* and `code`. Enough for an
 * agent's reply on a phone; tables, links and images are drawn as their text.
 */
private sealed interface MdBlock {
    data class Para(val text: String) : MdBlock
    data class Heading(val level: Int, val text: String) : MdBlock
    data class Item(val marker: String, val text: String) : MdBlock
    data class Code(val text: String) : MdBlock
}

private fun parse(source: String): List<MdBlock> {
    val blocks = mutableListOf<MdBlock>()
    val para = StringBuilder()
    fun flush() {
        if (para.isNotBlank()) blocks += MdBlock.Para(para.toString().trim())
        para.clear()
    }
    val lines = source.lines()
    var i = 0
    while (i < lines.size) {
        val line = lines[i]
        val trimmed = line.trimStart()
        when {
            trimmed.startsWith("```") -> {
                flush()
                val code = StringBuilder()
                i++
                while (i < lines.size && !lines[i].trimStart().startsWith("```")) { code.appendLine(lines[i]); i++ }
                blocks += MdBlock.Code(code.toString().trimEnd())
            }
            trimmed.startsWith("#") -> {
                flush()
                val level = trimmed.takeWhile { it == '#' }.length
                blocks += MdBlock.Heading(level, trimmed.drop(level).trim())
            }
            Regex("^[-*+] ").containsMatchIn(trimmed) -> { flush(); blocks += MdBlock.Item("•", trimmed.drop(2)) }
            Regex("^\\d+[.)] ").containsMatchIn(trimmed) -> {
                flush()
                val marker = trimmed.substringBefore(' ')
                blocks += MdBlock.Item(marker, trimmed.substringAfter(' '))
            }
            trimmed.isEmpty() -> flush()
            else -> { if (para.isNotEmpty()) para.append(' '); para.append(trimmed) }
        }
        i++
    }
    flush()
    return blocks
}

private val INLINE = Regex("(\\*\\*[^*]+\\*\\*|`[^`]+`|\\*[^*\\s][^*]*\\*|_[^_\\s][^_]*_)")

fun inlineMarkdown(text: String): AnnotatedString = buildAnnotatedString {
    var last = 0
    for (match in INLINE.findAll(text)) {
        append(text.substring(last, match.range.first))
        val token = match.value
        when {
            token.startsWith("**") -> withStyle(SpanStyle(fontWeight = FontWeight.SemiBold)) { append(token.removeSurrounding("**")) }
            token.startsWith("`") -> withStyle(SpanStyle(fontFamily = Fleet.mono, fontSize = 13.5.sp, background = Fleet.card, color = Fleet.text)) {
                append(" ${token.removeSurrounding("`")} ")
            }
            else -> withStyle(SpanStyle(fontStyle = FontStyle.Italic)) { append(token.substring(1, token.length - 1)) }
        }
        last = match.range.last + 1
    }
    append(text.substring(last))
}

@Composable
fun Markdown(text: String, modifier: Modifier = Modifier) {
    val blocks = remember(text) { parse(text) }
    Column(modifier, verticalArrangement = Arrangement.spacedBy(8.dp)) {
        blocks.forEach { block ->
            when (block) {
                is MdBlock.Para -> BasicText(inlineMarkdown(block.text), style = Fleet.body)
                is MdBlock.Heading -> BasicText(
                    inlineMarkdown(block.text),
                    style = Fleet.body.copy(fontWeight = FontWeight.SemiBold, fontSize = if (block.level <= 2) 17.sp else 15.sp),
                    modifier = Modifier.padding(top = 4.dp),
                )
                is MdBlock.Item -> Row {
                    BasicText(block.marker, style = Fleet.body.copy(color = Fleet.muted), modifier = Modifier.width(22.dp))
                    BasicText(inlineMarkdown(block.text), style = Fleet.body)
                }
                is MdBlock.Code -> BasicText(
                    block.text,
                    style = Fleet.code.copy(color = Fleet.text),
                    softWrap = false,
                    modifier = Modifier
                        .fillMaxWidth()
                        .background(Fleet.bg, RoundedCornerShape(Fleet.radiusButton))
                        .border(1.dp, Fleet.border, RoundedCornerShape(Fleet.radiusButton))
                        .horizontalScroll(rememberScrollState())
                        .padding(12.dp),
                )
            }
        }
    }
}
