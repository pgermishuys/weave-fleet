package io.tryweave.fleet.android

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.RemoteInput
import android.content.Context
import android.content.Intent
import android.os.Build
import android.util.Log
import io.tryweave.fleet.shared.model.SessionListItem
import io.tryweave.fleet.shared.model.SessionNotification
import io.tryweave.fleet.shared.store.FleetClient
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.flow.distinctUntilChanged
import kotlinx.coroutines.flow.map
import kotlinx.coroutines.launch

/**
 * Turns the hub's `session_notification`s into Android notifications with Allow once / Deny buttons (and an inline
 * reply for questions), and keeps an ongoing "Working" notification per working session, promoted to an Android 16
 * Live Update where the system allows it.
 *
 * This only runs while the app's process is alive and the hub is connected: Fleet pushes to phones with Web Push
 * only, so a closed app gets nothing until Fleet has an FCM / APNs gateway.
 */
class Notifier(private val context: Context, private val client: FleetClient) {
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main)
    private val manager = context.getSystemService(NotificationManager::class.java)
    private val shownWorking = mutableMapOf<String, Long>()

    fun start() {
        scope.launch { client.notifications.collect { onNotification(it) } }
        scope.launch {
            client.sessionsState.map { it.working }.distinctUntilChanged().collect { updateWorking(it) }
        }
    }

    fun stop() {
        shownWorking.keys.forEach { manager.cancel(workingId(it)) }
        scope.cancel()
    }

    private suspend fun onNotification(n: SessionNotification) {
        val builder = Notification.Builder(context, CHANNEL_ASKS)
            .setSmallIcon(R.drawable.ic_notification)
            .setColor(0xFF6366F1.toInt())
            .setContentTitle(n.title)
            .setContentText(n.body)
            .setStyle(Notification.BigTextStyle().bigText(n.body))
            .setAutoCancel(true)
            .setCategory(Notification.CATEGORY_MESSAGE)
            .setContentIntent(openIntent(n.sessionId))
        val id = askId(n.sessionId)
        when (n.kind ?: n.reason) {
            "permission" -> n.requestId?.let { requestId ->
                builder.addAction(action(ACTION_ALLOW, "Allow once", n.sessionId, requestId, id))
                builder.addAction(action(ACTION_DENY, "Deny", n.sessionId, requestId, id))
            }
            "question", "needs_you" -> client.pendingQuestionOf(n.sessionId)?.let { q ->
                builder.setContentText(q.question).setStyle(Notification.BigTextStyle().bigText(q.question))
                q.options.take(2).forEach { option ->
                    builder.addAction(action(ACTION_ANSWER, option, n.sessionId, q.requestId, id, answer = option))
                }
                if (q.custom || q.options.isEmpty()) {
                    val input = RemoteInput.Builder(KEY_ANSWER).setLabel("Answer").setChoices(q.options.take(3).toTypedArray<CharSequence>()).build()
                    builder.addAction(
                        Notification.Action.Builder(null, "Reply", replyIntent(n.sessionId, q.requestId, id))
                            .addRemoteInput(input)
                            .setAllowGeneratedReplies(false)
                            .build(),
                    )
                }
            }
        }
        manager.notify(id, builder.build())
    }

    /** One ongoing notification per working session; promoted to a Live Update on Android 16 when allowed. */
    private fun updateWorking(working: List<SessionListItem>) {
        val ids = working.map { it.id }.toSet()
        (shownWorking.keys - ids).forEach { manager.cancel(workingId(it)); shownWorking.remove(it) }
        for (item in working) {
            val since = shownWorking.getOrPut(item.id) { System.currentTimeMillis() }
            val builder = Notification.Builder(context, CHANNEL_WORKING)
                .setSmallIcon(R.drawable.ic_notification)
                .setContentTitle(item.title)
                .setContentText(listOf("Working", item.folder).filter { it.isNotEmpty() }.joinToString(" · "))
                .setOngoing(true)
                .setOnlyAlertOnce(true)
                .setWhen(since)
                .setShowWhen(true)
                .setUsesChronometer(true)
                .setCategory(Notification.CATEGORY_PROGRESS)
                .setContentIntent(openIntent(item.id))
            if (Build.VERSION.SDK_INT >= 36) {
                builder.setStyle(Notification.ProgressStyle().setProgressIndeterminate(true))
                // Notification.Builder#setRequestPromotedOngoing / setShortCriticalText arrive after API 36.0;
                // the extras are what they set.
                builder.extras.putBoolean(EXTRA_REQUEST_PROMOTED_ONGOING, true)
                builder.extras.putString(EXTRA_SHORT_CRITICAL_TEXT, "Working")
            }
            val notification = builder.build()
            if (Build.VERSION.SDK_INT >= 36) {
                Log.i(TAG, "live update for ${item.id}: promotable=${notification.hasPromotableCharacteristics()} canPostPromoted=${manager.canPostPromotedNotifications()}")
            }
            manager.notify(workingId(item.id), notification)
        }
    }

    private fun openIntent(sessionId: String): PendingIntent = PendingIntent.getActivity(
        context,
        sessionId.hashCode(),
        Intent(context, MainActivity::class.java).putExtra(EXTRA_SESSION, sessionId).addFlags(Intent.FLAG_ACTIVITY_SINGLE_TOP),
        PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE,
    )

    private fun action(kind: String, label: String, sessionId: String, requestId: String, notificationId: Int, answer: String? = null): Notification.Action {
        val intent = Intent(context, NotificationActionReceiver::class.java)
            .setAction(kind)
            .putExtra(EXTRA_SESSION, sessionId)
            .putExtra(EXTRA_REQUEST, requestId)
            .putExtra(EXTRA_NOTIFICATION, notificationId)
            .putExtra(EXTRA_ANSWER, answer)
        val pending = PendingIntent.getBroadcast(context, (kind + requestId + label).hashCode(), intent, PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE)
        return Notification.Action.Builder(null, label, pending).build()
    }

    private fun replyIntent(sessionId: String, requestId: String, notificationId: Int): PendingIntent {
        val intent = Intent(context, NotificationActionReceiver::class.java)
            .setAction(ACTION_REPLY)
            .putExtra(EXTRA_SESSION, sessionId)
            .putExtra(EXTRA_REQUEST, requestId)
            .putExtra(EXTRA_NOTIFICATION, notificationId)
        // RemoteInput fills the intent in, so this one has to be mutable.
        return PendingIntent.getBroadcast(context, (ACTION_REPLY + requestId).hashCode(), intent, PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_MUTABLE)
    }

    companion object {
        const val TAG = "FleetNotifier"
        const val CHANNEL_ASKS = "asks"
        const val CHANNEL_WORKING = "working"
        const val EXTRA_SESSION = "fleet.session"
        const val EXTRA_REQUEST = "fleet.request"
        const val EXTRA_NOTIFICATION = "fleet.notification"
        const val EXTRA_ANSWER = "fleet.answer"
        const val KEY_ANSWER = "answer"
        const val ACTION_ALLOW = "io.tryweave.fleet.ALLOW"
        const val ACTION_DENY = "io.tryweave.fleet.DENY"
        const val ACTION_ANSWER = "io.tryweave.fleet.ANSWER"
        const val ACTION_REPLY = "io.tryweave.fleet.REPLY"
        const val EXTRA_REQUEST_PROMOTED_ONGOING = "android.requestPromotedOngoing"
        const val EXTRA_SHORT_CRITICAL_TEXT = "android.shortCriticalText"

        fun askId(sessionId: String) = ("ask:$sessionId").hashCode()
        fun workingId(sessionId: String) = ("working:$sessionId").hashCode()

        fun createChannels(context: Context) {
            val manager = context.getSystemService(NotificationManager::class.java)
            manager.createNotificationChannel(
                NotificationChannel(CHANNEL_ASKS, "Needs you", NotificationManager.IMPORTANCE_HIGH).apply {
                    description = "An agent waits for your permission or answer, or finished its turn."
                },
            )
            manager.createNotificationChannel(
                NotificationChannel(CHANNEL_WORKING, "Working", NotificationManager.IMPORTANCE_DEFAULT).apply {
                    description = "Agents at work, shown as a Live Update."
                    setSound(null, null)
                    enableVibration(false)
                },
            )
        }
    }
}
