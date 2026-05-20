/**
 * Format utilities — display helpers extracted from mock-data.ts.
 * These are pure functions with no mock data dependencies.
 */

export function formatTokens(tokens: number): string {
  if (tokens >= 1000) return `${(tokens / 1000).toFixed(1)}k`;
  return tokens.toString();
}

export function formatCost(cost: number): string {
  return `$${cost.toFixed(2)}`;
}

export function formatDuration(seconds: number): string {
  const m = Math.floor(seconds / 60);
  const s = seconds % 60;
  if (m > 0) return `${m}m ${s}s`;
  return `${s}s`;
}

const timeOnlyFormatter = new Intl.DateTimeFormat("en-US", {
  hour: "numeric",
  minute: "2-digit",
  hour12: true,
});

const dateTimeFormatter = new Intl.DateTimeFormat("en-US", {
  month: "short",
  day: "numeric",
  hour: "numeric",
  minute: "2-digit",
  hour12: true,
});

const absoluteDateTimeFormatter = new Intl.DateTimeFormat("en-US", {
  month: "short",
  day: "numeric",
  year: "numeric",
  hour: "numeric",
  minute: "2-digit",
  hour12: true,
});

/**
 * Format a unix-ms timestamp for message display.
 * - Same calendar day as now → "2:34 PM" (time only)
 * - Different day → "Mar 1, 2:34 PM" (short month + day + time)
 * - Falsy / NaN → "" (graceful fallback)
 */
export function formatTimestamp(timestamp: number | undefined | null): string {
  if (!timestamp || isNaN(timestamp)) return "";

  const date = new Date(timestamp);
  const now = new Date();

  const sameDay =
    date.getFullYear() === now.getFullYear() &&
    date.getMonth() === now.getMonth() &&
    date.getDate() === now.getDate();

  return sameDay ? timeOnlyFormatter.format(date) : dateTimeFormatter.format(date);
}

/**
 * Format a unix-ms timestamp as a full absolute date+time string, always including the date.
 * e.g. "May 19, 2025, 11:16 PM"
 * - Falsy / NaN → "" (graceful fallback)
 */
export function formatAbsoluteTimestamp(timestamp: number | undefined | null): string {
  if (!timestamp || isNaN(timestamp)) return "";
  return absoluteDateTimeFormatter.format(new Date(timestamp));
}

/**
 * Format a unix-ms timestamp as a human-readable relative time string.
 * - < 30s  → "just now"
 * - < 60s  → "Xs ago"  (e.g. "45s ago")
 * - < 60m  → "Xm ago"  (e.g. "5m ago")
 * - < 24h  → "Xh ago"  (e.g. "2h ago")
 * - >= 24h → falls back to formatTimestamp(timestamp)
 *
 * @param timestamp - unix milliseconds
 * @param now       - reference time in unix ms (defaults to Date.now(); injectable for tests)
 */
export function formatRelativeTime(
  timestamp: number | Date | string,
  now?: number
): string {
  let ts: number;
  if (typeof timestamp === "number") {
    ts = timestamp;
  } else if (timestamp instanceof Date) {
    ts = timestamp.getTime();
  } else {
    // String — handle SQLite datetime format (may lack timezone indicator)
    const normalized =
      timestamp.endsWith("Z") || timestamp.includes("+")
        ? timestamp
        : timestamp + "Z";
    ts = new Date(normalized).getTime();
  }

  const reference = now ?? Date.now();
  const diffMs = reference - ts;
  const diffS = Math.floor(diffMs / 1000);

  if (diffS < 30) return "just now";
  if (diffS < 60) return `${diffS}s ago`;

  const diffM = Math.floor(diffS / 60);
  if (diffM < 60) return `${diffM}m ago`;

  const diffH = Math.floor(diffM / 60);
  if (diffH < 24) return `${diffH}h ago`;

  return formatTimestamp(ts);
}

export function getStatusColor(status: string): string {
  switch (status) {
    case "active":
    case "running":
      return "text-green-500";
    case "idle":
    case "paused":
      return "text-zinc-400";
    case "waiting_input":
      return "text-amber-500";
    case "completed":
    case "drained":
      return "text-blue-500";
    case "error":
    case "failed":
      return "text-red-500";
    case "pending":
    case "queued":
      return "text-zinc-500";
    case "draft":
      return "text-zinc-400";
    default:
      return "text-zinc-500";
  }
}

export function getStatusDot(status: string): string {
  switch (status) {
    case "active":
    case "running":
      return "bg-green-500";
    case "idle":
    case "paused":
      return "bg-zinc-400";
    case "waiting_input":
      return "bg-amber-500";
    case "completed":
    case "drained":
      return "bg-blue-500";
    case "error":
    case "failed":
      return "bg-red-500";
    case "pending":
    case "queued":
      return "bg-zinc-500";
    default:
      return "bg-zinc-500";
  }
}

// ─── Analytics Format Utilities ───────────────────────────────────────────────

/** Format large numbers with M/K suffix: 1234567 → "1.2M", 45678 → "45.7K" */
export function formatLargeNumber(n: number): string {
  if (n >= 1_000_000) return `${(n / 1_000_000).toFixed(1)}M`;
  if (n >= 1_000) return `${(n / 1_000).toFixed(1)}K`;
  return n.toLocaleString();
}

/** Format cost with appropriate precision: $0.004 → "$0.004", $1.50 → "$1.50", $1234 → "$1,234" */
export function formatAnalyticsCost(cost: number): string {
  if (cost === 0) return "$0.00";
  if (cost < 0.01) return `$${cost.toFixed(3)}`;
  if (cost >= 1000)
    return `$${cost.toLocaleString("en-US", {
      minimumFractionDigits: 2,
      maximumFractionDigits: 2,
    })}`;
  return `$${cost.toFixed(2)}`;
}

/** Format a date string as short display: "2025-01-15" → "Jan 15" */
export function formatShortDate(dateStr: string): string {
  const d = new Date(dateStr + "T00:00:00");
  return d.toLocaleDateString("en-US", { month: "short", day: "numeric" });
}
