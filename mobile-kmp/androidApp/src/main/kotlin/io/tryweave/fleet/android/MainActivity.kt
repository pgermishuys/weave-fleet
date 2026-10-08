package io.tryweave.fleet.android

import android.Manifest
import android.content.Intent
import android.content.pm.PackageManager
import android.os.Build
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.SystemBarStyle
import androidx.activity.compose.BackHandler
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.safeDrawing
import androidx.compose.foundation.layout.windowInsetsPadding
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.ExperimentalComposeUiApi
import androidx.compose.ui.Modifier
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.semantics.testTagsAsResourceId
import io.tryweave.fleet.android.ui.Fleet
import io.tryweave.fleet.android.ui.PairScreen
import io.tryweave.fleet.android.ui.SessionScreen
import io.tryweave.fleet.android.ui.SessionsScreen

class MainActivity : ComponentActivity() {
    private val app get() = application as FleetApp

    override fun onCreate(savedInstanceState: Bundle?) {
        enableEdgeToEdge(
            statusBarStyle = SystemBarStyle.dark(android.graphics.Color.TRANSPARENT),
            navigationBarStyle = SystemBarStyle.dark(android.graphics.Color.TRANSPARENT),
        )
        super.onCreate(savedInstanceState)
        handle(intent)
        if (Build.VERSION.SDK_INT >= 33 && checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED) {
            requestPermissions(arrayOf(Manifest.permission.POST_NOTIFICATIONS), 1)
        }
        setContent { Root(app) }
    }

    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        handle(intent)
    }

    private fun handle(intent: Intent?) {
        intent?.getStringExtra(Notifier.EXTRA_SESSION)?.let { app.openRequest.value = it }
    }
}

@OptIn(ExperimentalComposeUiApi::class)
@Composable
private fun Root(app: FleetApp) {
    val client by app.client.collectAsState()
    val request by app.openRequest.collectAsState()
    var open by rememberSaveable { mutableStateOf<String?>(null) }
    LaunchedEffect(request) {
        request?.let { open = it; app.openRequest.value = null }
    }
    // Maestro finds elements by resource id: expose the Compose test tags as ids.
    Box(Modifier.fillMaxSize().semantics { testTagsAsResourceId = true }.background(Fleet.bg).windowInsetsPadding(WindowInsets.safeDrawing)) {
        val current = client
        when {
            current == null -> PairScreen(app.pairing) { app.connect(it) }
            open != null -> {
                BackHandler { open = null }
                SessionScreen(current, open!!, onBack = { open = null })
            }
            else -> SessionsScreen(current, onOpen = { open = it }, onForget = { app.forget() })
        }
    }
}
