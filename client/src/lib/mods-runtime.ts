/** The install job's phases, as the server names them. */
export type BunInstallPhase = "downloading" | "verifying" | "extracting" | "succeeded" | "failed";

/** Why an install failed, in the server's words for it. */
export type BunInstallFailure = "offline" | "blocked" | "stopped" | "checksum" | "no-build" | "cancelled" | "other";

/** The Bun install running now, or the last one since Fleet started. */
export interface ModsRuntimeJob {
  phase: BunInstallPhase;
  /** install: no Fleet Bun yet. update: a newer release, the current Bun is safe. security: the current Bun is unsafe. */
  kind: "install" | "update" | "security";
  version: string;
  message?: string | null;
  reason?: BunInstallFailure | null;
  bytesReceived?: number | null;
  bytesTotal?: number | null;
  startedAt?: string | null;
  /** The version mods keep running on during an update or security install. */
  from?: string | null;
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
  oldestSafe?: string | null;
  note?: string | null;
  size?: number | null;
  hasBuild: boolean;
  installFolder: string;
  source: string;
}

/** What `GET /api/features/mods/runtime` answers: the Bun mods run on and the install state. */
export interface ModsRuntimeView {
  bun: ModsRuntimeBun | null;
  configuredPath?: string | null;
  configuredError?: string | null;
  configuredInConfig?: boolean;
  release: ModsRuntimeRelease;
  installedSize: number;
  job: ModsRuntimeJob | null;
  update?: { version: string; security: boolean } | null;
}

/** A Bun found on this computer (`GET …/found`). */
export interface BunCandidate {
  path: string;
  resolvedPath?: string;
  displayPath?: string;
  version?: string | null;
  status: string;
  message?: string | null;
}

export const MODS_RUNTIME_EVENT = "mods.runtime";

export function jobRunning(job: ModsRuntimeJob | null | undefined): job is ModsRuntimeJob {
  return job?.phase === "downloading" || job?.phase === "verifying" || job?.phase === "extracting";
}

/** Megabytes the way the row says them: bytes / 1,000,000, rounded. */
export function megabytes(bytes: number | null | undefined): number {
  return Math.round((bytes ?? 0) / 1_000_000);
}

/** A path that starts at the root: /opt/bun, C:\Tools\bun.exe or \\server\share\bun.exe. */
export function isAbsoluteBunPath(path: string): boolean {
  return path.startsWith("/") || /^[A-Za-z]:[\\/]/.test(path) || path.startsWith("\\\\");
}

export const BUN_PATH_HINT = "The path must start at the root, like /opt/tools/bun/bin/bun or C:\\Tools\\bun\\bun.exe.";

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

/** What a running install's heading says: the first install, an update, or a security fix. */
export function progressTitle(job: ModsRuntimeJob): string {
  if (job.phase === "verifying") return "Checking the download";
  if (job.phase === "extracting") return "Unpacking";
  if (job.kind === "security") return `Security fix: updating to Bun ${job.version}`;
  if (job.kind === "update") return `Updating to Bun ${job.version}`;
  return `Downloading Bun ${job.version}`;
}

/** The short cause inside a failure message, for a notice: "Fleet couldn't download it: the connection timed out." */
export function failureCause(job: ModsRuntimeJob): string {
  const message = job.message ?? "";
  if (job.reason === "offline") {
    const cause = /^Fleet couldn't reach [^:]+: (.+?)\./.exec(message)?.[1];
    if (cause) return `Fleet couldn't download it: ${cause}.`;
  }
  return message ? `Fleet couldn't download it. ${message}` : "Fleet couldn't download it.";
}
