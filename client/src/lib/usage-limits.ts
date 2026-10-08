/**
 * How much of their account's usage limits the user's harnesses have used: Claude Code on a claude.ai login reports its
 * 5-hour and weekly windows. Harnesses on an API key or a gateway report none, and nothing shows. The server keeps the
 * latest of each window (`GET /api/harnesses/usage`) and pushes changes as `harness.usage` on the sessions topic.
 */

export const HARNESS_USAGE_EVENT = "harness.usage";

/** From this much of a window used, the status bar shows it. */
export const USAGE_WARNING_THRESHOLD = 0.8;

export type UsageLimitStatus = "allowed" | "warning" | "rejected";

export interface UsageLimitWindow {
  /** "five_hour", "seven_day", "seven_day_opus", "seven_day_sonnet", or the harness's own name. */
  window: string;
  /** 0 to 1, when the harness says. */
  utilization: number | null;
  /** Epoch milliseconds, when the harness says. */
  resetsAt: number | null;
  status: UsageLimitStatus;
}

export interface HarnessUsage {
  harnessType: string;
  windows: UsageLimitWindow[];
}

/** One harness's limits from the wire, or null when they don't read. */
export function toHarnessUsage(raw: unknown): HarnessUsage | null {
  if (!raw || typeof raw !== "object") return null;
  const wire = raw as Record<string, unknown>;
  if (typeof wire.harnessType !== "string" || !Array.isArray(wire.windows)) return null;
  const windows = wire.windows.map(toWindow).filter((window): window is UsageLimitWindow => window !== null);
  return { harnessType: wire.harnessType, windows };
}

function toWindow(raw: unknown): UsageLimitWindow | null {
  if (!raw || typeof raw !== "object") return null;
  const wire = raw as Record<string, unknown>;
  if (typeof wire.window !== "string") return null;
  const utilization = typeof wire.utilization === "number" && Number.isFinite(wire.utilization)
    ? Math.min(1, Math.max(0, wire.utilization))
    : null;
  const resetsAt = typeof wire.resetsAt === "string" ? Date.parse(wire.resetsAt) : Number.NaN;
  const status: UsageLimitStatus = wire.status === "rejected" || wire.status === "warning" ? wire.status : "allowed";
  return { window: wire.window, utilization, resetsAt: Number.isFinite(resetsAt) ? resetsAt : null, status };
}

const WINDOW_ORDER = ["five_hour", "seven_day", "seven_day_opus", "seven_day_sonnet"];

/** Shortest window first, then the model-scoped ones; windows that reset already are left out. */
export function currentWindows(usage: HarnessUsage | null | undefined, now: number): UsageLimitWindow[] {
  if (!usage) return [];
  const rank = (window: string) => {
    const index = WINDOW_ORDER.indexOf(window);
    return index === -1 ? WINDOW_ORDER.length : index;
  };
  return usage.windows
    .filter((window) => window.resetsAt === null || window.resetsAt > now)
    .sort((a, b) => rank(a.window) - rank(b.window) || a.window.localeCompare(b.window));
}

/** "5-hour limit", "Weekly limit", "Weekly Opus limit". */
export function windowLabel(window: string): string {
  switch (window) {
    case "five_hour":
      return "5-hour limit";
    case "seven_day":
      return "Weekly limit";
    case "seven_day_opus":
      return "Weekly Opus limit";
    case "seven_day_sonnet":
      return "Weekly Sonnet limit";
    default:
      return `${window.replace(/_/g, " ")} limit`;
  }
}

/** The status bar's short name: "5 h", "week", "Opus week". */
export function windowShortLabel(window: string): string {
  switch (window) {
    case "five_hour":
      return "5 h";
    case "seven_day":
      return "week";
    case "seven_day_opus":
      return "Opus week";
    case "seven_day_sonnet":
      return "Sonnet week";
    default:
      return window.replace(/_/g, " ");
  }
}

/** "resets 14:05" within a day, "resets Mon" within a week, else "resets 12 Oct". */
export function resetLabel(resetsAt: number | null, now: number, locale?: string): string | null {
  if (resetsAt === null) return null;
  const at = new Date(resetsAt);
  const hours = (resetsAt - now) / 3_600_000;
  if (hours < 24) return `resets ${at.toLocaleTimeString(locale, { hour: "2-digit", minute: "2-digit" })}`;
  if (hours < 24 * 6) return `resets ${at.toLocaleDateString(locale, { weekday: "short" })}`;
  return `resets ${at.toLocaleDateString(locale, { day: "numeric", month: "short" })}`;
}

/** "82%", or null when the harness didn't say. */
export function percentLabel(window: UsageLimitWindow): string | null {
  if (window.status === "rejected") return "100%";
  return window.utilization === null ? null : `${Math.round(window.utilization * 100)}%`;
}

/** Close to its limit, or at it: what the status bar speaks up about. */
export function isNearLimit(window: UsageLimitWindow): boolean {
  return window.status === "rejected" || (window.utilization ?? 0) >= USAGE_WARNING_THRESHOLD;
}

/** The window the status bar shows: the fullest one at or over the threshold, a used-up one first. */
export function windowToFlag(usage: HarnessUsage | null | undefined, now: number): UsageLimitWindow | null {
  const near = currentWindows(usage, now).filter(isNearLimit);
  if (near.length === 0) return null;
  return near.reduce((worst, window) => {
    if ((window.status === "rejected") !== (worst.status === "rejected")) return window.status === "rejected" ? window : worst;
    return (window.utilization ?? 0) > (worst.utilization ?? 0) ? window : worst;
  });
}
