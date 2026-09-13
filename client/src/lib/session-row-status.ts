/**
 * A session's status, shown only on its row in the sessions list.
 * States that need the user get words and colour. A working session shows no
 * word, because its glyph animates. Everything else shows how long ago the
 * session was last active.
 */
import type { SessionListItem } from "@/api/client";

export type SessionRowTone = "attention" | "working" | "retry" | "error" | "quiet";

export interface SessionRowStatus {
  /** Short text on the right of the row. Empty while the session works. */
  label: string;
  tone: SessionRowTone;
  /** The state in words, for the glyph's tooltip and screen readers. */
  description: string;
}

/** Compact age for list rows: "now", "4m", "2h", "3d", "5w". */
export function formatCompactAge(timestamp: number | string, now: number): string {
  const ts = typeof timestamp === "number" ? timestamp : Number(timestamp) || Date.parse(timestamp);
  if (!Number.isFinite(ts)) return "";

  const minutes = Math.floor(Math.max(0, now - ts) / 60_000);
  if (minutes < 1) return "now";
  if (minutes < 60) return `${minutes}m`;

  const hours = Math.floor(minutes / 60);
  if (hours < 24) return `${hours}h`;

  const days = Math.floor(hours / 24);
  if (days < 7) return `${days}d`;

  return `${Math.floor(days / 7)}w`;
}

export function sessionRowStatus(item: SessionListItem, now: number): SessionRowStatus {
  switch (item.sessionStatus) {
    case "waiting_input":
      return { label: "Needs input", tone: "attention", description: "Needs input" };
    case "error":
      return { label: "Error", tone: "error", description: "Error" };
    case "active":
      if (item.activityStatus === "retry") {
        const attempt = item.retryAttempt;
        return attempt
          ? { label: `Retry ${attempt}`, tone: "retry", description: `Retrying (attempt ${attempt})` }
          : { label: "Retrying", tone: "retry", description: "Retrying" };
      }
      return {
        label: "",
        tone: "working",
        description: item.activityStatus === "delegating" ? "Delegating" : "Working",
      };
    case "completed":
      return { label: "Done", tone: "quiet", description: "Done" };
    case "disconnected":
      return { label: "Offline", tone: "quiet", description: "Offline" };
    default: {
      const time = item.session.time;
      return {
        label: formatCompactAge(time?.updated ?? time?.created ?? "", now),
        tone: "quiet",
        description: "Idle",
      };
    }
  }
}
