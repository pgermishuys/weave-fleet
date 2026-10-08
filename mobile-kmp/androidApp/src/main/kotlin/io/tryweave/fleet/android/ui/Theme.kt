package io.tryweave.fleet.android.ui

import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.font.Font
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import io.tryweave.fleet.android.R

/** Weave Fleet's dark tokens (client/src/styles), not Material's. */
object Fleet {
    val bg = Color(0xFF0D0D10)
    val panel = Color(0xFF141418)
    val card = Color(0xFF1B1B20)
    val border = Color(0x13FFFFFF) // rgba(255,255,255,0.075)
    val borderStrong = Color(0x24FFFFFF)
    val text = Color(0xFFE8E8EC)
    val muted = Color(0xFF8E8E9A)
    val faint = Color(0xFF5E5E6A)
    val accent = Color(0xFF6366F1)
    val running = Color(0xFF22C55E)
    val waiting = Color(0xFFF59E0B)
    val error = Color(0xFFEF4444)

    val radiusCard = 10.dp
    val radiusButton = 8.dp
    val radiusPanel = 12.dp
    val row = 44.dp

    val inter = FontFamily(
        Font(R.font.inter_regular, FontWeight.Normal),
        Font(R.font.inter_medium, FontWeight.Medium),
        Font(R.font.inter_semibold, FontWeight.SemiBold),
        Font(R.font.inter_bold, FontWeight.Bold),
    )
    val mono = FontFamily(
        Font(R.font.jetbrains_mono_regular, FontWeight.Normal),
        Font(R.font.jetbrains_mono_medium, FontWeight.Medium),
    )

    val body = TextStyle(fontFamily = inter, fontSize = 15.sp, lineHeight = 22.sp, color = text)
    val small = TextStyle(fontFamily = inter, fontSize = 13.sp, lineHeight = 18.sp, color = muted)
    val title = TextStyle(fontFamily = inter, fontSize = 16.sp, lineHeight = 21.sp, fontWeight = FontWeight.SemiBold, color = text)
    val display = TextStyle(fontFamily = inter, fontSize = 26.sp, lineHeight = 32.sp, fontWeight = FontWeight.Bold, color = text, letterSpacing = (-0.3).sp)
    val label = TextStyle(fontFamily = inter, fontSize = 15.sp, lineHeight = 20.sp, fontWeight = FontWeight.Medium, color = text)
    val code = TextStyle(fontFamily = mono, fontSize = 13.5.sp, lineHeight = 19.sp, color = muted)
}
