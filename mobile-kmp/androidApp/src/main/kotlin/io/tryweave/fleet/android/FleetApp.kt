package io.tryweave.fleet.android

import android.app.Application
import io.tryweave.fleet.shared.model.Credentials
import io.tryweave.fleet.shared.platform.AndroidSecureStore
import io.tryweave.fleet.shared.store.FleetClient
import io.tryweave.fleet.shared.store.Pairing
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow

/** Holds the paired machine's client for the life of the process, and turns its hub events into notifications. */
class FleetApp : Application() {
    lateinit var pairing: Pairing
        private set

    private val _client = MutableStateFlow<FleetClient?>(null)
    val client: StateFlow<FleetClient?> = _client.asStateFlow()

    /** A session a notification asked to open. */
    val openRequest = MutableStateFlow<String?>(null)

    private var notifier: Notifier? = null

    override fun onCreate() {
        super.onCreate()
        pairing = Pairing(AndroidSecureStore(this))
        Notifier.createChannels(this)
        pairing.saved()?.let(::connect)
    }

    fun connect(credentials: Credentials) {
        _client.value?.close()
        notifier?.stop()
        val client = FleetClient(credentials)
        client.start()
        _client.value = client
        notifier = Notifier(this, client).also { it.start() }
    }

    fun forget() {
        notifier?.stop()
        _client.value?.close()
        _client.value = null
        pairing.forget()
    }
}
