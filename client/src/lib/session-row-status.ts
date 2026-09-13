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

/**
 * How far a quiet session's row fades back: 0 at full contrast, 1 after a day
 * without activity, 2 after three days. Rows never move; only contrast changes.
 */
export type SessionRowDim = 0 | 1 | 2;

const DAY_MS = 24 * 60 * 60_000;

/** Statuses where something is going on. Only these get a glyph on the row. */
const LIVE_STATUSES = new Set(["active", "waiting_input", "error"]);

function toTimestamp(timestamp: number | string): number {
  return typeof timestamp === "number" ? timestamp : Number(timestamp) || Date.parse(timestamp);
}

/** Compact age for list rows: "now", "4m", "2h", "3d", "5w". */
export function formatCompactAge(timestamp: number | string, now: number): string {
  const ts = toTimestamp(timestamp);
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

export function isSessionLive(item: SessionListItem): boolean {
  return LIVE_STATUSES.has(item.sessionStatus);
}

/** Live sessions never dim. Everything else fades by its last activity. */
export function sessionRowDim(item: SessionListItem, now: number): SessionRowDim {
  if (isSessionLive(item)) return 0;

  const time = item.session.time;
  const ts = toTimestamp(time?.updated ?? time?.created ?? "");
  if (!Number.isFinite(ts)) return 0;

  const age = now - ts;
  if (age >= 3 * DAY_MS) return 2;
  if (age >= DAY_MS) return 1;
  return 0;
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
