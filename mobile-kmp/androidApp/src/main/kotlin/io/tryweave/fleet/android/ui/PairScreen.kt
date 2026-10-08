package io.tryweave.fleet.android.ui

import android.os.Build
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.imePadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.BasicText
import androidx.compose.foundation.text.BasicTextField
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.SolidColor
import androidx.compose.ui.platform.testTag
import androidx.compose.ui.res.painterResource
import androidx.compose.foundation.Image
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.input.KeyboardCapitalization
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.unit.dp
import io.tryweave.fleet.android.BuildConfigLite
import io.tryweave.fleet.android.R
import io.tryweave.fleet.shared.model.Credentials
import io.tryweave.fleet.shared.store.Pairing

@Composable
fun PairScreen(pairing: Pairing, onPaired: (Credentials) -> Unit) {
    var address by remember { mutableStateOf(BuildConfigLite.defaultAddress) }
    var code by remember { mutableStateOf("") }
    var name by remember { mutableStateOf(Build.MODEL ?: "Android phone") }
    var busy by remember { mutableStateOf(false) }
    var error by remember { mutableStateOf<String?>(null) }

    Column(
        Modifier.fillMaxSize().imePadding().verticalScroll(rememberScrollState()).padding(horizontal = 20.dp, vertical = 28.dp),
        verticalArrangement = Arrangement.spacedBy(14.dp),
    ) {
        Image(painterResource(R.drawable.ic_mark), contentDescription = null, modifier = Modifier.size(36.dp))
        Spacer(Modifier.height(4.dp))
        BasicText("Pair with a machine", style = Fleet.display)
        BasicText(
            "On your computer, open Fleet → Settings → Machines → This machine → Add a phone. Type its address and the 8-character code, or paste the pairing link.",
            style = Fleet.body.copy(color = Fleet.muted),
        )
        Spacer(Modifier.height(6.dp))
        Field("Address", address, "https://my-machine.example.com", KeyboardType.Uri, tag = "address") { address = it }
        Field("Code", code, "XXXX-XXXX", KeyboardType.Ascii, mono = true, caps = true, tag = "code") { code = it.uppercase().take(9) }
        Field("This phone's name", name, "Pixel", KeyboardType.Text, tag = "device-name") { name = it }
        error?.let { BasicText(it, style = Fleet.small.copy(color = Fleet.error)) }
        Spacer(Modifier.height(4.dp))
        FleetButton(
            if (busy) "Pairing…" else "Pair",
            modifier = Modifier.fillMaxWidth().testTag("pair"),
            primary = true,
            enabled = !busy && address.isNotBlank() && (code.length >= 8 || address.contains("#p=")),
        ) {
            busy = true
            error = null
            pairing.pair(address, code, name.ifBlank { "Android phone" }, "android") { credentials, problem ->
                busy = false
                if (credentials != null) onPaired(credentials) else error = problem
            }
        }
        BasicText("The code works once and runs out after a few minutes. Fleet keeps only a hash of this phone's token.", style = Fleet.small)
    }
}

@Composable
private fun Field(
    label: String,
    value: String,
    placeholder: String,
    type: KeyboardType,
    mono: Boolean = false,
    caps: Boolean = false,
    tag: String,
    onChange: (String) -> Unit,
) {
    val shape = RoundedCornerShape(Fleet.radiusCard)
    val style: TextStyle = if (mono) Fleet.code.copy(color = Fleet.text, fontSize = Fleet.body.fontSize) else Fleet.body
    Column(verticalArrangement = Arrangement.spacedBy(6.dp)) {
        BasicText(label, style = Fleet.small)
        BasicTextField(
            value = value,
            onValueChange = onChange,
            singleLine = true,
            textStyle = style,
            cursorBrush = SolidColor(Fleet.accent),
            keyboardOptions = KeyboardOptions(keyboardType = type, capitalization = if (caps) KeyboardCapitalization.Characters else KeyboardCapitalization.None, autoCorrectEnabled = false),
            modifier = Modifier.fillMaxWidth().testTag(tag).background(Fleet.card, shape).border(1.dp, Fleet.border, shape).padding(horizontal = 14.dp, vertical = 13.dp),
            decorationBox = { inner ->
                if (value.isEmpty()) BasicText(placeholder, style = style.copy(color = Fleet.faint))
                inner()
            },
        )
    }
}
