/** The Bun install running now, or the last one since Fleet started. */
export interface ModsRuntimeJob {
  phase: "downloading" | "verifying" | "extracting" | "succeeded" | "failed";
  version: string;
  message?: string | null;
  /** Why it failed: offline, blocked, stopped, checksum, no-build, cancelled or other. */
  reason?: string | null;
  bytesReceived?: number | null;
  bytesTotal?: number | null;
  startedAt?: string | null;
}

export interface ModsRuntimeBun {
  path: string;
  displayPath: string;
  source: "installed" | "configured";
  version: string;
  safe: boolean;
  message?: string | null;
}

export interface ModsRuntimeRelease {
  version: string;
  size?: number | null;
  hasBuild: boolean;
  installFolder: string;
  source: string;
}

/** What `GET /api/features/mods/runtime` answers: the Bun mods run on and the install state. */
export interface ModsRuntimeView {
  bun: ModsRuntimeBun | null;
  configuredPath?: string | null;
  release: ModsRuntimeRelease;
  job: ModsRuntimeJob | null;
}

export const MODS_RUNTIME_EVENT = "mods.runtime";

export function jobRunning(job: ModsRuntimeJob | null | undefined): boolean {
  return job?.phase === "downloading" || job?.phase === "verifying" || job?.phase === "extracting";
}

/** Megabytes the way the row says them: bytes / 1,000,000, rounded. */
export function megabytes(bytes: number | null | undefined): number {
  return Math.round((bytes ?? 0) / 1_000_000);
}

/** "40 seconds", "3 minutes", "2 hours": how long ago a job started. */
export function elapsedText(startedAt: string | null | undefined, now: number): string {
  const started = startedAt ? Date.parse(startedAt) : Number.NaN;
  const seconds = Number.isNaN(started) ? 0 : Math.max(0, Math.round((now - started) / 1000));
  if (seconds < 90) return `${seconds} ${seconds === 1 ? "second" : "seconds"}`;
  const minutes = Math.round(seconds / 60);
  if (minutes < 90) return `${minutes} minutes`;
  const hours = Math.round(minutes / 60);
  return `${hours} ${hours === 1 ? "hour" : "hours"}`;
}

/** What a running install's heading says. */
export function progressTitle(job: ModsRuntimeJob): string {
  if (job.phase === "verifying") return "Checking the download";
  if (job.phase === "extracting") return "Unpacking";
  return `Downloading Bun ${job.version}`;
}
