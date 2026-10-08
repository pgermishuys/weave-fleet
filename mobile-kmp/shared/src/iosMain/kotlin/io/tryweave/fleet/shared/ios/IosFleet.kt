package io.tryweave.fleet.shared.ios

import io.tryweave.fleet.shared.model.Credentials
import io.tryweave.fleet.shared.platform.KeychainSecureStore
import io.tryweave.fleet.shared.reducer.PhoneBlock
import io.tryweave.fleet.shared.reducer.PhoneItem
import io.tryweave.fleet.shared.store.FleetClient
import io.tryweave.fleet.shared.store.Pairing

/**
 * The door Swift comes in by. Everything the iPhone app needs is reachable from here with plain calls and callbacks:
 * no Flow, no suspend functions, no generics across the boundary.
 */
object IosFleet {
    fun pairing(): Pairing = Pairing(KeychainSecureStore())

    fun client(credentials: Credentials): FleetClient = FleetClient(credentials).also { it.start() }

    /** Swift can't switch over a Kotlin sealed interface exhaustively; this names the case. */
    fun kindOf(block: PhoneBlock): String = when (block) {
        is PhoneBlock.User -> "user"
        is PhoneBlock.Text -> "text"
        is PhoneBlock.Steps -> "steps"
        is PhoneBlock.Subagent -> "subagent"
        is PhoneBlock.Question -> "question"
        is PhoneBlock.Error -> "error"
    }

    fun toolsOf(item: PhoneItem): PhoneItem.Tools? = item as? PhoneItem.Tools

    fun blockOf(item: PhoneItem): PhoneBlock? = (item as? PhoneItem.Block)?.block
}
