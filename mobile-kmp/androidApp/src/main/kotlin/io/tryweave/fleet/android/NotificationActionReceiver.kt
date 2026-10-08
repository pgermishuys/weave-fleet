package io.tryweave.fleet.android

import android.app.Notification
import android.app.NotificationManager
import android.app.RemoteInput
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import io.tryweave.fleet.shared.api.withFleetApi
import io.tryweave.fleet.shared.model.PermissionReply
import io.tryweave.fleet.shared.platform.AndroidSecureStore
import io.tryweave.fleet.shared.store.Pairing
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch

/**
 * Answers from the notification shade, without opening the app: reads the device token from the Keystore and calls
 * the REST endpoint itself (works even when the hub connection is gone).
 */
class NotificationActionReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        val sessionId = intent.getStringExtra(Notifier.EXTRA_SESSION) ?: return
        val requestId = intent.getStringExtra(Notifier.EXTRA_REQUEST) ?: return
        val notificationId = intent.getIntExtra(Notifier.EXTRA_NOTIFICATION, 0)
        val typed = RemoteInput.getResultsFromIntent(intent)?.getCharSequence(Notifier.KEY_ANSWER)?.toString()
        val credentials = Pairing(AndroidSecureStore(context)).saved() ?: return
        val pending = goAsync()

        CoroutineScope(Dispatchers.IO).launch {
            val outcome = runCatching {
                withFleetApi(credentials) { api ->
                    when (intent.action) {
                        Notifier.ACTION_ALLOW -> api.replyToPermission(sessionId, requestId, PermissionReply.Once).let { "Allowed" }
                        Notifier.ACTION_DENY -> api.replyToPermission(sessionId, requestId, PermissionReply.Reject).let { "Denied" }
                        Notifier.ACTION_ANSWER -> intent.getStringExtra(Notifier.EXTRA_ANSWER)!!.let { api.answerQuestion(sessionId, requestId, listOf(listOf(it))); "Answered: $it" }
                        Notifier.ACTION_REPLY -> typed!!.let { api.answerQuestion(sessionId, requestId, listOf(listOf(it))); "Answered: $it" }
                        else -> error("Unknown action")
                    }
                }
            }
            val manager = context.getSystemService(NotificationManager::class.java)
            // Replace the ask with what happened (an inline reply has to be answered with an update, or it spins).
            manager.notify(
                notificationId,
                Notification.Builder(context, Notifier.CHANNEL_ASKS)
                    .setSmallIcon(R.drawable.ic_notification)
                    .setContentTitle(outcome.getOrNull() ?: "Couldn't send that")
                    .setContentText(outcome.exceptionOrNull()?.message ?: "The agent carries on.")
                    .setTimeoutAfter(if (outcome.isSuccess) 4_000 else 0)
                    .setOnlyAlertOnce(true)
                    .build(),
            )
            pending.finish()
        }
    }
}
