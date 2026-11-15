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

/** A Bun Fleet found on this computer that mods can run on. */
export interface ModsRuntimeFound {
  /** What `POST /use` takes. */
  path: string;
  displayPath: string;
  version: string;
}

/** What `GET /api/features/mods/runtime` answers: the Bun mods run on and the install state. */
export interface ModsRuntimeView {
  bun: ModsRuntimeBun | null;
  configuredPath?: string | null;
  release: ModsRuntimeRelease;
  job: ModsRuntimeJob | null;
  /** Buns on this computer to pick from; empty when one is in use, the configuration sets one, or in cloud mode. */
  found?: ModsRuntimeFound[];
}

export const MODS_RUNTIME_EVENT = "mods.runtime";

export function jobRunning(job: ModsRuntimeJob | null | undefined): boolean {
  return job?.phase === "downloading" || job?.phase === "verifying" || job?.phase === "extracting";
}

/** Megabytes the way the row says them: bytes / 1,000,000, rounded. */
export function megabytes(bytes: number | null | undefined): number {
  return Math.round((bytes ?? 0) / 1_000_000);
}

/** What a running install's heading says. */
export function progressTitle(job: ModsRuntimeJob): string {
  if (job.phase === "verifying") return "Checking the download";
  if (job.phase === "extracting") return "Unpacking";
  return `Downloading Bun ${job.version}`;
}
