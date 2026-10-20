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
  /** Whether it can restart itself into a staged update; missing on a Fleet older than the restart. */
  canRestart?: boolean;
  /** Why not, in words for the page. */
  restartUnavailableReason?: string | null;
  /** An update that failed to start there and was undone, until a later one succeeds. */
  rolledBack?: { from: string; to: string; reason: string; at: string } | null;
}

/** A machine's part of a rollout, as `GET /api/update/rollout` and its `update.rollout` event say it. */
export interface RolloutStep {
  machineId: string;
  name: string;
  state: "pending" | "checking" | "waiting" | "restarting" | "done" | "rolled-back" | "no-return" | "skipped";
  detail?: string | null;
  fromVersion?: string | null;
  toVersion?: string | null;
  waitingSessions: number;
}

export interface Rollout {
  id: string;
  status: "running" | "done" | "stopped" | "cancelled";
  startedAt: string;
  finishedAt?: string | null;
  steps: RolloutStep[];
  summary?: string | null;
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
  /** An update that didn't start there and was undone. */
  | "rolledback"
  /** Part of a rollout under way. */
  | "pending"
  | "waiting"
  | "restarting"
  | "updated"
  | "skipped"
  | "noreturn"
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

  // Its launcher put the old version back; it won't fetch that release again, so nothing is waiting.
  const undone = update.rolledBack;
  if (undone && !staged && isNewerVersion(undone.to, version)) {
    return { kind: "rolledback", label: `${undone.to} didn't start here (${undone.reason}) · went back to ${version ?? undone.from}`, tone: "bad", target: null };
  }

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
  /** What it offers for restarting into its update (the desktop app never does). */
  restart: RestartOffer | null;
  /** Home is the Fleet this page belongs to; it restarts only from its own row. */
  isHome: boolean;
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

/** What a row offers for restarting into the update it has: a button, or the reason it can't. Null when it has no update to restart into. */
export type RestartOffer =
  | { kind: "button"; label: string; disabled: boolean }
  | { kind: "unavailable"; reason: string };

/**
 * `canRestart` and its reason come from the machine; a Fleet older than the restart sends neither. A busy remote machine
 * still offers it: the rollout waits for its sessions. This machine can't wait, so its button waits for them to end.
 */
export function restartOffer(state: FleetUpdateState, update: MachineUpdate | null, isHome: boolean): RestartOffer | null {
  if (state.kind !== "ready" && state.kind !== "busy") return null;
  if (!update) return null;
  if (update.canRestart === undefined) return { kind: "unavailable", reason: "too old to restart itself · restart it by hand" };
  if (!update.canRestart) return { kind: "unavailable", reason: update.restartUnavailableReason ?? "can't restart itself" };
  if (isHome) return { kind: "button", label: `Restart into ${state.target ?? "the update"}`, disabled: state.kind === "busy" };
  return { kind: "button", label: state.kind === "busy" ? "Restart when idle" : `Restart into ${state.target ?? "the update"}`, disabled: false };
}

/** The state a row shows while a rollout has it in hand; the machine's own words once the rollout is over. */
export function describeRolloutStep(step: RolloutStep): FleetUpdateState {
  const to = step.toVersion ?? null;
  switch (step.state) {
    case "checking":
      return { kind: "checking", label: "checking…", tone: "info", target: to };
    case "waiting":
      return { kind: "waiting", label: step.detail ?? "waiting for its sessions to finish", tone: "warn", target: to };
    case "restarting":
      return { kind: "restarting", label: step.detail ?? "restarting…", tone: "info", target: to };
    case "done":
      return { kind: "updated", label: `updated to ${to ?? "the new version"} · healthy`, tone: "ok", target: null };
    case "rolled-back":
      return { kind: "rolledback", label: `${step.detail ?? "went back to its old version"} · the rollout stopped here`, tone: "bad", target: null };
    case "no-return":
      return { kind: "noreturn", label: `${step.detail ?? "didn't come back"} · the rollout stopped here`, tone: "bad", target: null };
    case "skipped":
      return { kind: "skipped", label: `skipped · ${step.detail ?? "left alone"}`, tone: "muted", target: null };
    default:
      return { kind: "pending", label: "waiting its turn", tone: "muted", target: to };
  }
}

/** The line under the buttons once a rollout has ended: what it did, or why it stopped. Null while it runs. */
export function describeRolloutOutcome(rollout: Rollout | null): { text: string; tone: FleetUpdateState["tone"] } | null {
  if (!rollout || rollout.status === "running") return null;
  const done = rollout.steps.filter((step) => step.state === "done").map((step) => step.name);
  const skipped = rollout.steps.filter((step) => step.state === "skipped").map((step) => step.name);
  const left = rollout.steps.filter((step) => step.state === "pending").map((step) => step.name);
  if (rollout.status === "done") {
    const text = done.length ? `Updated ${done.join(" and ")}.` : "Nothing needed updating.";
    return { text: skipped.length ? `${text} Skipped ${skipped.join(", ")}.` : text, tone: "ok" };
  }
  const why = rollout.summary ?? "The rollout stopped.";
  return { text: left.length ? `${why} ${left.join(" and ")} ${left.length === 1 ? "was" : "were"} left alone.` : why, tone: rollout.status === "stopped" ? "bad" : "muted" };
}
