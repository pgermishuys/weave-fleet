package io.tryweave.fleet.shared.platform

import io.ktor.client.engine.HttpClientEngine

/** OkHttp on Android, NSURLSession (Darwin) on iPhone. Both do WebSockets. */
expect fun httpEngine(): HttpClientEngine

/**
 * Where the device token lives: the Android Keystore (an AES key that never leaves it, the token encrypted with it)
 * and the iOS Keychain. One string per key.
 */
interface SecureStore {
    fun get(key: String): String?
    fun put(key: String, value: String)
    fun remove(key: String)
}
