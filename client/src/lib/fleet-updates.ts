/**
 * Updates in Settings → Machines: what each Fleet you run says about its own version. Pure mapping from what a machine reported
 * (`update` in `GET /api/machine`, or the desktop app's bridge) to the row the page draws, so every state has one test.
 */
import type { DesktopUpdateState } from "@/lib/desktop";
import { formatRelativeTime } from "@/lib/format-utils";

/** Settings → Features: shows each machine's version, update rule and Restart in Settings → Machines. Off until turned on. */
export const FLEET_UPDATES_PREFERENCE_KEY = "FleetUpdates";

/** Settings → Machines: the rule the user chose for every machine, unless one has its own. */
export const UPDATE_RULE_DEFAULT_KEY = "FleetUpdateRule";

/** A JSON object: machine key (`home` for this one) to the rule that machine follows. */
export const UPDATE_RULE_OVERRIDES_KEY = "FleetUpdateRules";

/** This device's note of the version each machine last reported, so a machine that can't be reached still says what it was on. */
const LAST_SEEN_VERSIONS_KEY = "fleet.machineVersions";

export function readLastSeenVersions(): Record<string, string> {
  try {
    const parsed: unknown = JSON.parse(globalThis.localStorage?.getItem(LAST_SEEN_VERSIONS_KEY) ?? "{}");
    if (!parsed || typeof parsed !== "object") return {};
    return Object.fromEntries(Object.entries(parsed).filter((entry): entry is [string, string] => typeof entry[1] === "string"));
  } catch {
    return {};
  }
}

export function writeLastSeenVersions(versions: Record<string, string>): void {
  try {
    globalThis.localStorage?.setItem(LAST_SEEN_VERSIONS_KEY, JSON.stringify(versions));
  } catch {
    // No storage (private window, quota): an unreachable machine just won't say its version.
  }
}

/** When a Fleet restarts itself into an update it has downloaded. */
export type UpdateRule = "ask" | "idle" | "overnight";

export const UPDATE_RULES: { value: UpdateRule; label: string; short: string; detail: string }[] = [
  { value: "ask", label: "Ask me first", short: "Ask me", detail: "Each Fleet waits here until you restart it." },
  {
    value: "idle",
    label: "Update each machine when nothing is working on it",
    short: "When nothing is working",
    detail: "It waits for its turns to finish, restarts within a minute or two, and tells you after. Desktop apps still ask, because you're using them.",
  },
  { value: "overnight", label: "Only overnight, 02:00–05:00 on that machine's clock", short: "Overnight", detail: "For machines that run long jobs during the day." },
];

export function isUpdateRule(value: unknown): value is UpdateRule {
  return value === "ask" || value === "idle" || value === "overnight";
}

/** The overrides as the preference keeps them; anything that isn't a known rule is dropped. */
export function parseRuleOverrides(json: string | null | undefined): Record<string, UpdateRule> {
  try {
    const parsed: unknown = JSON.parse(json || "{}");
    if (!parsed || typeof parsed !== "object") return {};
    return Object.fromEntries(Object.entries(parsed).filter((entry): entry is [string, UpdateRule] => isUpdateRule(entry[1])));
  } catch {
    return {};
  }
}

/** The rule a machine follows: its own, else the one chosen for all, else what suits its kind (a node updates when idle, a Fleet asks). */
export function effectiveRule(overridden: UpdateRule | null, chosenDefault: UpdateRule | null, kind: "fleet" | "node"): UpdateRule {
  return overridden ?? chosenDefault ?? (kind === "node" ? "idle" : "ask");
}

/** The update a Fleet last restarted into, and what started the restart. */
export interface LastUpdate {
  from: string;
  to: string;
  at: string;
  trigger: "idle" | "overnight" | "manual" | "rollout";
}

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
  /** When it restarts itself into an update; missing on a Fleet older than the rule. */
  rule?: UpdateRule;
  ruleSource?: "default" | "set";
  /** The update it last restarted into; kept until the next one. */
  lastUpdate?: LastUpdate | null;
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
  /** Sessions in a turn on that machine, including ones waiting for an answer (see `busySessions`). */
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
  return `${count} ${count === 1 ? "session" : "sessions"} working or waiting for you`;
}

/** Sessions a restart would cut off: working, or stopped on a question or a permission ask (still in their turn). */
export function busySessions(sessions: { working: number; needsYou?: number } | null | undefined): number {
  return (sessions?.working ?? 0) + (sessions?.needsYou ?? 0);
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
  /** The rule this row follows and the override on it; null where there is none to set (the desktop app always asks; an old Fleet can't take one). */
  rule: { effective: UpdateRule; overridden: UpdateRule | null } | null;
  /** Sessions in a turn on that machine (working or waiting for an answer); missing where unknown. */
  working?: number;
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

/** How long after a Fleet updated itself the home page still says so. */
const UPDATED_NOTICE_DAYS = 3;

const UPDATED_BY: Record<LastUpdate["trigger"], string> = {
  idle: "when nothing was working",
  overnight: "overnight",
  manual: "when you restarted it",
  rollout: "by Update machines now",
};

/** The notice for a Fleet that has updated: "falcon updated to 0.59.1", once per machine and version. Null when it isn't news. */
export function describeUpdatedNotice(
  machineKey: string,
  name: string,
  runningVersion: string | null,
  last: LastUpdate | null | undefined,
  now: Date,
): { id: string; title: string; body: string; version: string } | null {
  if (!last || !runningVersion || last.to.replace(/^v/, "") !== runningVersion.replace(/^v/, "")) return null;
  const at = new Date(last.at);
  if (Number.isNaN(at.getTime()) || now.getTime() - at.getTime() > UPDATED_NOTICE_DAYS * 86_400_000) return null;
  const clock = at.toLocaleTimeString([], { hour: "2-digit", minute: "2-digit", hour12: false });
  return {
    id: `fleet-updated:${machineKey}:${last.to}`,
    title: `${name} updated to ${last.to}`,
    body: `At ${clock}, ${UPDATED_BY[last.trigger] ?? "by itself"}.`,
    version: last.to,
  };
}

/** The steps of a rollout that are done with, one way or another. */
const FINISHED_STEPS: RolloutStep["state"][] = ["done", "rolled-back", "no-return", "skipped"];
const ACTIVE_STEPS: RolloutStep["state"][] = ["checking", "waiting", "restarting"];

/**
 * The one status bar chip for machine updates: how many machines can update, or where a rollout is. Null when neither.
 * Counts machines only (the desktop app's own update has its own notice).
 */
export function describeUpdateChip(waiting: number, rollout: Rollout | null = null): string | null {
  if (rollout?.status === "running" && rollout.steps.length > 0) {
    const finished = rollout.steps.filter((step) => FINISHED_STEPS.includes(step.state)).length;
    return `Updating machines · ${Math.min(finished + 1, rollout.steps.length)} of ${rollout.steps.length}`;
  }
  return waiting > 0 ? `↑ ${waiting} ${waiting === 1 ? "machine" : "machines"} can update` : null;
}

function sessionCount(count: number): string {
  return `${count} ${count === 1 ? "session" : "sessions"}`;
}

/** The step a rollout is working on now, if any. */
function activeStep(rollout: Rollout | null): RolloutStep | undefined {
  return rollout?.status === "running" ? rollout.steps.find((step) => ACTIVE_STEPS.includes(step.state)) : undefined;
}

export interface VersionLine {
  /** "Fleet 0.59.0", or "Fleet" when the machine won't say. */
  version: string;
  /** The version it would move to; null when it isn't moving. */
  target: string | null;
  text: string;
  tone: FleetUpdateState["tone"];
  /** Whether the per-machine rule menu belongs on the line: the machine reports a rule and can be reached. */
  ruleMenu: boolean;
}

/** What a machine downloaded does by itself under its rule. */
function selfUpdateText(rule: UpdateRule, working: number, state: FleetUpdateState): string {
  if (rule === "ask") return "waiting for you to restart it";
  if (rule === "overnight") return "updates itself tonight, 02:00–05:00";
  return state.kind === "busy" && working > 0
    ? `updates itself when ${working === 1 ? "its session finishes" : `its ${working} sessions finish`}`
    : "updates itself in a minute";
}

/** The line along the bottom of a machine card in Settings → Machines: its version, and where it is with updating. */
export function describeVersionLine(row: FleetUpdateRow, rollout: Rollout | null): VersionLine {
  const { state } = row;
  const step = rollout?.status === "running" ? rollout.steps.find((candidate) => candidate.machineId === row.key) : undefined;
  const base = (version: string | null, target: string | null, text: string, tone: FleetUpdateState["tone"]): VersionLine => ({
    version: version ? `Fleet ${version}` : "Fleet",
    target,
    text,
    tone,
    ruleMenu: row.rule !== null && state.kind !== "unreachable",
  });

  if (state.kind === "unreachable") {
    return base(row.version, null, row.version ? `can't be reached · last seen on ${row.version}` : "can't be reached", "muted");
  }
  if (step) {
    const target = step.toVersion ?? state.target;
    switch (step.state) {
      case "waiting":
        return base(row.version, target, `Updating: ${step.detail ?? (step.waitingSessions > 0 ? `waiting for ${sessionCount(step.waitingSessions)} to finish` : "waiting for its sessions to finish")}`, "warn");
      case "restarting":
        return base(row.version, target, `Updating: ${step.detail ?? "restarting…"}`, "info");
      case "checking":
        return base(row.version, target, "Updating: checking…", "info");
      case "pending": {
        // The machine before it in the queue: the one being handled now, or another still waiting its turn.
        const before = rollout!.steps[rollout!.steps.indexOf(step) - 1];
        const ahead = before && (before.state === "pending" || ACTIVE_STEPS.includes(before.state)) ? before : activeStep(rollout);
        return base(row.version, target, ahead ? `Updating: next, after ${ahead.name}` : "Updating: waiting its turn", "info");
      }
      case "done":
        return base(step.toVersion ?? row.version, null, describeRolloutStep(step).label, "ok");
      default:
        return base(row.version, null, describeRolloutStep(step).label, describeRolloutStep(step).tone);
    }
  }
  if (state.kind === "ready" || state.kind === "busy") {
    const downloaded = `${state.target ?? "the update"} downloaded`;
    if (row.restart?.kind === "unavailable") return base(row.version, state.target, `${downloaded} · ${row.restart.reason}`, state.tone);
    if (!row.rule) return base(row.version, state.target, state.label, state.tone);
    return base(row.version, state.target, `${downloaded} · ${selfUpdateText(row.rule.effective, row.working ?? 0, state)}`, state.tone);
  }
  return base(row.version, state.target, state.label, state.tone);
}

/** The newest `checkedAt` any machine reported, or null when none has checked. */
export function newestCheckedAt(times: readonly (string | null | undefined)[]): string | null {
  let newest: string | null = null;
  let newestMs = -Infinity;
  for (const time of times) {
    const ms = time ? Date.parse(time) : Number.NaN;
    if (!Number.isNaN(ms) && ms > newestMs) {
      newest = time ?? null;
      newestMs = ms;
    }
  }
  return newest;
}

export interface ReleaseLine {
  kind: "available" | "current" | "none";
  /** The bold start of the line; empty when there is none. */
  lead: string;
  text: string;
}

/** The strip at the top of Settings → Machines when no rollout runs. `checkedAt` is the newest check any machine made. */
export function describeReleaseLine(
  summary: { latest: string | null; waiting: number; downloaded: number },
  checkedAt: string | null,
  now?: number,
): ReleaseLine {
  const { latest, waiting, downloaded } = summary;
  if (!latest) return { kind: "none", lead: "", text: "Fleet hasn't found a release yet." };
  if (waiting === 0) {
    const checked = checkedAt ? ` Checked ${formatRelativeTime(checkedAt, now)}.` : "";
    return { kind: "current", lead: "", text: `Every machine that can be reached is on Fleet ${latest}.${checked}` };
  }
  const lead = `Fleet ${latest} is out.`;
  if (downloaded === 0) {
    return { kind: "available", lead, text: `${waiting} ${waiting === 1 ? "machine" : "machines"} can update; ${waiting === 1 ? "it isn't" : "they aren't"} downloaded yet.` };
  }
  if (downloaded === waiting) {
    return {
      kind: "available",
      lead,
      text: waiting === 1
        ? "1 machine has it downloaded and switches when it restarts."
        : `${waiting} machines have it downloaded and switch when they restart.`,
    };
  }
  return {
    kind: "available",
    lead,
    text: `${downloaded} of ${waiting} machines that can update ${downloaded === 1 ? "has" : "have"} it downloaded and switch when they restart.`,
  };
}

/**
 * The strip while a rollout runs: its headline, who is being handled (for "Stop after falcon"), and a sentence on each
 * machine. `unreachable` holds the machines that can't be reached now: the rollout will skip them when it gets there.
 */
export function describeRolloutNarrative(rollout: Rollout, unreachable: ReadonlySet<string> = new Set()): { title: string; active: string | null; text: string } {
  const to = rollout.steps.find((step) => step.toVersion)?.toVersion;
  const title = `Updating machines${to ? ` to ${to}` : ""}, one at a time.`;
  const sentences: string[] = [];
  let live: string[] = [];
  let firstPending = true;
  const flush = () => {
    if (live.length) sentences.push(`${live.join("; ")}.`);
    live = [];
  };
  for (const step of rollout.steps) {
    switch (step.state) {
      case "waiting":
        live.push(step.waitingSessions > 0 ? `${step.name} is waiting for ${sessionCount(step.waitingSessions)}` : `${step.name} is waiting for its sessions`);
        break;
      case "checking":
        live.push(`${step.name} is being checked`);
        break;
      case "restarting":
        live.push(`${step.name} is restarting`);
        break;
      case "pending":
        if (unreachable.has(step.machineId)) {
          flush();
          sentences.push(`${step.name} can't be reached, so it'll be skipped.`);
          break;
        }
        live.push(firstPending ? `${step.name} is next` : `${step.name} is queued`);
        firstPending = false;
        break;
      case "done":
        flush();
        sentences.push(`${step.name} is done.`);
        break;
      case "skipped":
        flush();
        sentences.push(step.detail && /reach/i.test(step.detail) ? `${step.name} can't be reached, so it's skipped.` : `${step.name} is skipped${step.detail ? `: ${step.detail}` : ""}.`);
        break;
      case "rolled-back":
        flush();
        sentences.push(`${step.name} went back to its old version.`);
        break;
      case "no-return":
        flush();
        sentences.push(`${step.name} didn't come back.`);
        break;
    }
  }
  flush();
  return { title, active: activeStep(rollout)?.name ?? null, text: sentences.join(" ") };
}
