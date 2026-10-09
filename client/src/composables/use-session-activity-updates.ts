import { onMounted, onUnmounted } from "vue"
import { onGlobalEvent } from "@/composables/use-signalr-socket"
import { liveTarget } from "@/lib/machine-target"
import { useSessionsStore } from "@/stores/sessions"
import type { DomainEvent } from "@/lib/domain-events"
import { deriveSessionStatus } from "@/lib/session-status"
import { toScheduledRetry } from "@/lib/turn-retry"

/**
 * Subscribes to global "sessions" topic to receive activity_status events
 * and update the sessions store in real-time.
 */
export function useSessionActivityUpdates(): void {
  const sessionsStore = useSessionsStore()
  let unsubscribe: (() => void) | null = null

  onMounted(() => {
    unsubscribe = onGlobalEvent(liveTarget(), "sessions", (event: DomainEvent) => {
      // When Fleet tries a turn a limit stopped again: the session's row says so.
      if (event.type === "session.retry") {
        sessionsStore.patchSession(event.payload.sessionId, { scheduledRetry: toScheduledRetry(event.payload.retry) })
        return
      }

      if (event.type === "activity_status") {
        const payload = event.payload as {
          sessionId?: string
          activityStatus?: string
          capabilities?: unknown
          attempt?: number | null
          maxAttempts?: number | null
          message?: string | null
          next?: string | null
        }

        if (payload.sessionId && payload.activityStatus) {
          // Find the current session to check its current sessionStatus
          const currentSession = sessionsStore.sessions.find(
            (s) => s.session.id === payload.sessionId
          )

          // Derive the new sessionStatus from activityStatus, preserving lifecycle states
          const newSessionStatus = deriveSessionStatus(
            payload.activityStatus,
            currentSession?.sessionStatus
          )

          // Why and when a retrying session tries again, until it's working again.
          const retrying = payload.activityStatus === "retry"

          // Update both activityStatus and sessionStatus in the store
          sessionsStore.patchSession(payload.sessionId, {
            activityStatus: payload.activityStatus,
            sessionStatus: newSessionStatus,
            retryAttempt: retrying ? payload.attempt ?? null : null,
            retryMaxAttempts: retrying ? payload.maxAttempts ?? null : null,
            retryMessage: retrying ? payload.message ?? null : null,
            retryNext: retrying ? payload.next ?? null : null,
          })

          // Clear any optimistic busy state override
          sessionsStore.clearSessionStateOverride(payload.sessionId)
        }
      }
    })
  })

  onUnmounted(() => {
    if (unsubscribe) {
      unsubscribe()
    }
  })
}
