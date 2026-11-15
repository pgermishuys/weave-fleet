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

/** A size in bytes as "812 KB", "340 MB" or "2.1 GB"; one decimal from a gigabyte up. */
export function formatBytes(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`;
  if (bytes < 1024 ** 2) return `${Math.round(bytes / 1024)} KB`;
  if (bytes < 1024 ** 3) return `${Math.round(bytes / 1024 ** 2)} MB`;
  return `${(bytes / 1024 ** 3).toFixed(1)} GB`;
}
