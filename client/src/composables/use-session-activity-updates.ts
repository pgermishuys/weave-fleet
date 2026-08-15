import { onMounted, onUnmounted } from "vue"
import { onGlobalEvent } from "@/composables/use-signalr-socket"
import { useSessionsStore } from "@/stores/sessions"
import type { DomainEvent } from "@/lib/domain-events"

/**
 * Maps activityStatus to sessionStatus following the server's DeriveSessionStatus logic.
 * Only maps activity-driven states; lifecycle states like "stopped", "completed", "error", "disconnected"
 * are preserved and not clobbered by activity events.
 * 
 * Server mapping (SessionEndpoints.cs ~line 667-677):
 * - If session.Status is "stopped" or "completed" → preserve it (lifecycle state)
 * - Otherwise:
 *   - activityStatus "idle" → sessionStatus "idle"
 *   - activityStatus "busy"/"delegating"/"retry"/"waiting_input" → sessionStatus "active"
 */
function deriveSessionStatus(activityStatus: string, currentSessionStatus?: string): string {
  // Preserve lifecycle states — don't clobber them with activity-driven states
  if (currentSessionStatus === "stopped" || 
      currentSessionStatus === "completed" || 
      currentSessionStatus === "error" || 
      currentSessionStatus === "disconnected" ||
      currentSessionStatus === "resuming") {
    return currentSessionStatus
  }

  // Map activity status to session status
  switch (activityStatus) {
    case "idle":
      return "idle"
    case "busy":
    case "delegating":
    case "retry":
    case "waiting_input":
      return "active"
    default:
      // Unknown activity status — default to active to be safe
      return "active"
  }
}

/**
 * Subscribes to global "sessions" topic to receive activity_status events
 * and update the sessions store in real-time.
 */
export function useSessionActivityUpdates(): void {
  const sessionsStore = useSessionsStore()
  let unsubscribe: (() => void) | null = null

  onMounted(() => {
    unsubscribe = onGlobalEvent("sessions", (event: DomainEvent) => {
      if (event.type === "activity_status") {
        const payload = event.payload as {
          sessionId?: string
          activityStatus?: string
          capabilities?: unknown
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

          // Update both activityStatus and sessionStatus in the store
          sessionsStore.patchSession(payload.sessionId, {
            activityStatus: payload.activityStatus,
            sessionStatus: newSessionStatus,
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
