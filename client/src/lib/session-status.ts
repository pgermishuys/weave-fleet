/**
 * Maps activityStatus to sessionStatus following the server's DeriveSessionStatus logic.
 * Only maps activity-driven states; lifecycle states like "stopped", "completed", "error", "disconnected"
 * are preserved and not clobbered by activity events.
 *
 * Server mapping (SessionEndpoints.DeriveSessionStatus):
 * - If session.Status is "stopped" or "completed" → preserve it (lifecycle state)
 * - Otherwise:
 *   - activityStatus "idle" → sessionStatus "idle"
 *   - activityStatus "waiting_input" → sessionStatus "waiting_input"
 *   - activityStatus "busy"/"delegating"/"retry" → sessionStatus "active"
 */
export function deriveSessionStatus(activityStatus: string, currentSessionStatus?: string): string {
  // Preserve lifecycle states — don't clobber them with activity-driven states
  if (currentSessionStatus === "stopped" ||
      currentSessionStatus === "completed" ||
      currentSessionStatus === "error" ||
      currentSessionStatus === "disconnected") {
    return currentSessionStatus
  }

  // Map activity status to session status
  switch (activityStatus) {
    case "idle":
      return "idle"
    // A session stopped on a question needs the user, so it gets its own status rather than "active".
    case "waiting_input":
      return "waiting_input"
    case "busy":
    case "delegating":
    case "retry":
      return "active"
    default:
      // Unknown activity status — default to active to be safe
      return "active"
  }
}
