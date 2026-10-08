package io.tryweave.fleet.shared.store

import kotlinx.coroutines.Job

/** What a `watch { }` returns, so Swift can stop listening without touching coroutines. */
class WatchHandle internal constructor(private val job: Job) {
    fun cancel() = job.cancel()
}
