/**
 * The short status shown on the right of a session row in the sessions list.
 * States that need the user get words and colour; everything else shows how
 * long ago the session was last active.
 */
import type { SessionListItem } from "@/api/client";

export type SessionRowTone = "attention" | "working" | "error" | "quiet";

export interface SessionRowStatus {
  label: string;
  tone: SessionRowTone;
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
      return { label: "Needs input", tone: "attention" };
    case "error":
      return { label: "Error", tone: "error" };
    case "resuming":
      return { label: "Resuming", tone: "working" };
    case "active":
      return { label: item.activityStatus === "delegating" ? "Delegating" : "Working", tone: "working" };
    case "completed":
      return { label: "Done", tone: "quiet" };
    case "stopped":
      return { label: "Paused", tone: "quiet" };
    case "disconnected":
      return { label: "Offline", tone: "quiet" };
    default: {
      const time = item.session.time;
      return { label: formatCompactAge(time?.updated ?? time?.created ?? "", now), tone: "quiet" };
    }
  }
}
