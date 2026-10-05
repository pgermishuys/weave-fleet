/** "just now", "3 min ago", "2 h ago", "yesterday", "3 days ago": how long ago, as the phone screens say it. */
export function ago(timestamp: number | null | undefined, now: number): string {
  if (!timestamp) return "";
  const minutes = Math.floor(Math.max(0, now - timestamp) / 60_000);
  if (minutes < 1) return "just now";
  if (minutes < 60) return `${minutes} min ago`;
  const hours = Math.floor(minutes / 60);
  if (hours < 24) return `${hours} h ago`;
  const days = Math.floor(hours / 24);
  return days === 1 ? "yesterday" : `${days} days ago`;
}

/** "1m 12s", "6m", "2h 5m", "13d 6h": how long something has been going. */
export function duration(fromTimestamp: number | null | undefined, now: number): string {
  if (!fromTimestamp) return "";
  const seconds = Math.floor(Math.max(0, now - fromTimestamp) / 1000);
  if (seconds < 60) return `${seconds}s`;
  const minutes = Math.floor(seconds / 60);
  if (minutes < 10) return `${minutes}m ${seconds % 60}s`;
  if (minutes < 60) return `${minutes}m`;
  const hours = Math.floor(minutes / 60);
  if (hours < 24) return `${hours}h ${minutes % 60}m`;
  return `${Math.floor(hours / 24)}d ${hours % 24}h`;
}

/** "14:07", the clock time, for "Since you looked at 14:07" and "last heard 14:07". */
export function clock(timestamp: number): string {
  return new Date(timestamp).toLocaleTimeString(undefined, { hour: "2-digit", minute: "2-digit" });
}
