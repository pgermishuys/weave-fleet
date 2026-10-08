package io.tryweave.fleet.shared.store

import io.tryweave.fleet.shared.api.FleetApi
import io.tryweave.fleet.shared.api.createHttpClient
import io.tryweave.fleet.shared.model.Credentials
import io.tryweave.fleet.shared.model.FleetJson
import io.tryweave.fleet.shared.platform.SecureStore
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.launch

/** Pairing and the saved credentials, with callbacks so Swift doesn't need to call suspend functions. */
class Pairing(private val store: SecureStore) {
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main)

    fun saved(): Credentials? = store.get(KEY)?.let { runCatching { FleetJson.decodeFromString(Credentials.serializer(), it) }.getOrNull() }

    fun forget() = store.remove(KEY)

    /**
     * Pairs with a typed address and code. The address may also be a pairing QR URL (`…/pair#p=…`), in which case
     * the code is ignored and the URL's secret is used.
     */
    fun pair(address: String, code: String, deviceName: String, platform: String, onResult: (Credentials?, String?) -> Unit) {
        scope.launch {
            val http = createHttpClient()
            val qr = FleetApi.parsePairingUrl(address)
            val result = runCatching {
                if (qr != null) FleetApi.pair(http, qr.url, null, qr.secret, deviceName, platform)
                else FleetApi.pair(http, normalize(address), code, null, deviceName, platform)
            }
            http.close()
            result.onSuccess { store.put(KEY, FleetJson.encodeToString(Credentials.serializer(), it)) }
            onResult(result.getOrNull(), result.exceptionOrNull()?.let { it.message ?: "Couldn't pair" })
        }
    }

    private fun normalize(address: String): String {
        val trimmed = address.trim().trimEnd('/')
        return if (trimmed.startsWith("http://") || trimmed.startsWith("https://")) trimmed else "https://$trimmed"
    }

    companion object {
        const val KEY = "fleet.credentials"
    }
}
