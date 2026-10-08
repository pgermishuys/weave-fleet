package io.tryweave.fleet.shared.platform

import io.ktor.client.engine.HttpClientEngine
import io.ktor.client.engine.darwin.Darwin

actual fun httpEngine(): HttpClientEngine = Darwin.create()
