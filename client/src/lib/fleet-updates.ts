/**
 * Settings → Updates: what each Fleet you run says about its own version. Pure mapping from what a machine reported
 * (`update` in `GET /api/machine`, or the desktop app's bridge) to the row the page draws, so every state has one test.
 */
import type { DesktopUpdateState } from "@/lib/desktop";

/** Settings → Features: shows Settings → Updates. Off until turned on. */
export const FLEET_UPDATES_PREFERENCE_KEY = "FleetUpdates";

/** What `update` in `GET /api/machine` says: the words are the ones `GET /api/update/status` uses. */
export interface MachineUpdate {
  status: "unknown" | "uptodate" | "available" | "downloading" | "staged" | "error" | "managed";
  latestVersion: string | null;
  /** The version downloaded and waiting for a restart. */
  stagedVersion: string | null;
  checkedAt: string | null;
}

export type FleetUpdateKind =
  | "current"
  | "available"
  | "downloading"
  | "ready"
  | "busy"
  | "managed"
  | "unreachable"
  | "error"
  | "unchecked"
  | "unsupported"
  | "checking"
  /** The desktop app can't install it itself (unsigned macOS, .deb): it links to the download. */
  | "manual";

export interface FleetUpdateState {
  kind: FleetUpdateKind;
  label: string;
  tone: "ok" | "warn" | "info" | "bad" | "muted";
  /** The version it would move to, when an update is waiting or on its way. */
  target: string | null;
}

export interface FleetUpdateInput {
  version: string | null;
  /** Null from a Fleet older than the field. */
  update: MachineUpdate | null;
  /** Sessions in a turn on that machine. */
  working: number;
  reachable: boolean;
}

/** True when `a` is a later release than `b`; versions are `major.minor.patch`. */
export function isNewerVersion(a: string | null | undefined, b: string | null | undefined): boolean {
  if (!a || !b) return false;
  const parse = (v: string) => v.replace(/^v/, "").split(/[.+-]/).slice(0, 3).map((part) => Number.parseInt(part, 10) || 0);
  const [x, y] = [parse(a), parse(b)];
  for (let i = 0; i < 3; i++) {
    if ((x[i] ?? 0) !== (y[i] ?? 0)) return (x[i] ?? 0) > (y[i] ?? 0);
  }
  return false;
}

function sessionsWorking(count: number): string {
  return `${count} ${count === 1 ? "session" : "sessions"} working`;
}

/** The state of a Fleet reached through `GET /api/machine`. */
export function describeMachineUpdate(input: FleetUpdateInput): FleetUpdateState {
  const { version, update, working, reachable } = input;
  if (!reachable) return { kind: "unreachable", label: "can't be reached", tone: "muted", target: null };
  if (!update) return { kind: "unsupported", label: "too old to report updates", tone: "muted", target: null };
  if (update.status === "managed") {
    return { kind: "managed", label: "managed by the desktop app", tone: "muted", target: null };
  }

  const staged = isNewerVersion(update.stagedVersion, version) ? update.stagedVersion : null;
  const latest = isNewerVersion(update.latestVersion, version) ? update.latestVersion : null;

  if (update.status === "downloading") {
    return { kind: "downloading", label: `downloading ${latest ?? "the update"}…`, tone: "info", target: latest };
  }
  if (staged || update.status === "staged") {
    const target = staged ?? latest;
    const ready = `${target ?? "the update"} downloaded`;
    return working > 0
      ? { kind: "busy", label: `${ready} · busy: ${sessionsWorking(working)}`, tone: "warn", target }
      : { kind: "ready", label: `${ready} · waiting for a restart`, tone: "warn", target };
  }
  if (update.status === "available" && latest) {
    return { kind: "available", label: `${latest} is out, not downloaded yet`, tone: "warn", target: latest };
  }
  if (update.status === "error") return { kind: "error", label: "couldn't check for updates", tone: "bad", target: null };
  if (update.status === "unknown") return { kind: "unchecked", label: "not checked yet", tone: "muted", target: null };
  return { kind: "current", label: "up to date", tone: "ok", target: null };
}

/** The state of the desktop app itself, from its bridge. Null state: the app hasn't said yet. */
export function describeDesktopUpdate(state: DesktopUpdateState | null): FleetUpdateState {
  const target = state?.version ?? null;
  switch (state?.status) {
    case "idle":
      return { kind: "current", label: "up to date", tone: "ok", target: null };
    case "checking":
      return { kind: "checking", label: "checking for updates…", tone: "info", target: null };
    case "downloading":
      return { kind: "downloading", label: `downloading ${target ?? "the update"}…${state.percent != null ? ` ${state.percent}%` : ""}`, tone: "info", target };
    case "ready":
      return { kind: "ready", label: `${target ?? "the update"} downloaded · waiting for a restart`, tone: "warn", target };
    case "available":
      return { kind: "manual", label: `${target ?? "an update"} is out · download it and replace this app`, tone: "warn", target };
    case "error":
      return { kind: "error", label: "update check failed", tone: "bad", target: null };
    case "off":
      return { kind: "unsupported", label: "this build doesn't update itself", tone: "muted", target: null };
    default:
      return { kind: "unchecked", label: "not checked yet", tone: "muted", target: null };
  }
}

/** Whether an update is on its way or waiting for this Fleet: what the nav badge counts. */
export function hasUpdateWaiting(state: FleetUpdateState): boolean {
  return ["available", "downloading", "ready", "busy", "manual"].includes(state.kind);
}

export interface FleetUpdateRow {
  key: string;
  name: string;
  /** What it is: the desktop app, a full Fleet, or a node (`fleet node`, no web app). */
  kind: "app" | "fleet" | "node";
  os: string | null;
  /** The version it runs; null when it won't say. */
  version: string | null;
  /** The newest release it knows of. */
  latest: string | null;
  /** "this machine" or its address. */
  where: string;
  isLive: boolean;
  state: FleetUpdateState;
}

const OS_NAMES: Record<string, string> = { linux: "Linux", macos: "macOS", windows: "Windows" };

export function osLabel(os: string | null | undefined): string | null {
  return os ? (OS_NAMES[os] ?? os) : null;
}

/** The newest release any Fleet knows of, and how many Fleets have an update waiting or already downloaded. */
export function summarizeRows(rows: readonly FleetUpdateRow[]): { latest: string | null; waiting: number; downloaded: number; unreachable: number } {
  let latest: string | null = null;
  for (const row of rows) {
    for (const candidate of [row.latest, row.state.target]) {
      if (candidate && (latest === null || isNewerVersion(candidate, latest))) latest = candidate;
    }
  }
  return {
    latest,
    waiting: rows.filter((row) => hasUpdateWaiting(row.state)).length,
    downloaded: rows.filter((row) => row.state.kind === "ready" || row.state.kind === "busy").length,
    unreachable: rows.filter((row) => row.state.kind === "unreachable").length,
  };
}
