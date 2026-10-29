/**
 * Mission control as data: every machine's sessions as tiles, grouped by folder, machine or state, and what waits on
 * the user. Pure: the feeds (`mission-control-feed.ts`) fill it in, the screen draws it. No Vue.
 */
import type { SessionListItem } from "@/api/client";
import { FINISHED_FOR_MS, sessionFolder, type InboxAsk } from "@/lib/phone/inbox";
import { isTopLevel, sessionBucket, sessionUpdatedAt } from "@/lib/needs-you";
import type { FeedStatus } from "@/lib/machine-feed";
import { askPreviewWording } from "@/lib/tools";

/** The switch in Settings → Labs. Off, there's no rail item, no screen and no request. */
export const MISSION_CONTROL_PREFERENCE_KEY = "features.mission-control.enabled";

/** What a machine says a session is doing this second (`GET /api/sessions/now`). */
export interface SessionNow {
  text: string;
  kind: "tool" | "thinking" | "reply";
  at: string;
}

export type TileState = "needs-you" | "working" | "idle" | "error";
export type Grouping = "folder" | "machine" | "state";

/** One machine as mission control knows it. */
export interface MissionMachine {
  id: string;
  name: string;
  status: FeedStatus;
  sessions: SessionListItem[];
  /** Asks by session id. */
  asks: Record<string, InboxAsk>;
  /** Now lines by session id; empty when the machine is too old to say. */
  now: Record<string, SessionNow>;
}

export interface Tile {
  key: string;
  machineId: string;
  machineName: string;
  sessionId: string;
  title: string;
  folder: string | null;
  branch: string | null;
  state: TileState;
  harness: string;
  /** The harness's own name (`opencode2`), which says what it can do. */
  harnessType: string;
  model: string | null;
  /** What it's doing: the question, the ask, the machine's now line, else the state in words. */
  now: string;
  todos: { done: number; total: number } | null;
  helpers: number;
  ask: InboxAsk | null;
  updatedAt: number;
  /** The machine didn't answer last time: this is how it was then. */
  stale: boolean;
}

export interface TileGroup {
  key: string;
  label: string;
  tiles: Tile[];
}

const HARNESS_NAMES: Record<string, string> = {
  opencode: "OpenCode",
  opencode2: "OpenCode 2",
  "claude-code": "Claude Code",
  copilot: "GitHub Copilot",
  pi: "Pi",
};

const STATE_LABEL: Record<TileState, string> = {
  "needs-you": "Needs you",
  working: "Working",
  idle: "Idle",
  error: "Stopped with an error",
};

const STATE_ORDER: TileState[] = ["needs-you", "error", "working", "idle"];

function stateOf(session: SessionListItem): TileState {
  if (session.sessionStatus === "error") return "error";
  const bucket = sessionBucket(session);
  return bucket === "needs-you" ? "needs-you" : bucket === "working" ? "working" : "idle";
}

/** The line a tile shows for a session that waits on an ask. */
function askLine(ask: InboxAsk): string {
  if (ask.kind === "question") return `? ${ask.question.question}`;
  const { lead, detail } = askPreviewWording(ask.ask);
  return detail ? `${lead} ${detail}` : lead;
}

function nowLine(state: TileState, ask: InboxAsk | null, line: SessionNow | undefined): string {
  if (state === "needs-you" && ask) return askLine(ask);
  if (state === "working" && line) return line.text;
  if (state === "idle" && line?.kind === "reply") return line.text;
  return STATE_LABEL[state];
}

function tile(machine: MissionMachine, session: SessionListItem): Tile {
  const state = stateOf(session);
  const id = session.session.id;
  const ask = machine.asks[id] ?? null;
  return {
    key: `${machine.id}:${id}`,
    machineId: machine.id,
    machineName: machine.name,
    sessionId: id,
    title: session.session.title?.trim() || "Untitled session",
    folder: sessionFolder(session),
    branch: session.branch?.trim() || null,
    state,
    harness: HARNESS_NAMES[session.harnessType] ?? session.harnessType,
    harnessType: session.harnessType,
    model: session.selectedModel?.modelID ?? null,
    now: nowLine(state, ask, machine.now[id]),
    todos: session.progress && session.progress.total > 0 ? { done: session.progress.done, total: session.progress.total } : null,
    helpers: session.runningWorkCount ?? 0,
    ask,
    updatedAt: sessionUpdatedAt(session),
    stale: machine.status === "unreachable",
  };
}

/**
 * Every machine's top-level sessions that wait on the user or work, and those that went quiet in the last day, newest
 * first.
 */
export function buildTiles(machines: readonly MissionMachine[], now: number, quietFor = FINISHED_FOR_MS): Tile[] {
  const tiles: Tile[] = [];
  for (const machine of machines) {
    for (const session of machine.sessions) {
      if (!isTopLevel(session) || session.retentionStatus === "archived") continue;
      const entry = tile(machine, session);
      if (entry.state === "idle" && now - entry.updatedAt > quietFor) continue;
      tiles.push(entry);
    }
  }
  return tiles.sort((a, b) => b.updatedAt - a.updatedAt);
}

/** What waits on the user, newest first. */
export function needsYou(tiles: readonly Tile[]): Tile[] {
  return tiles.filter((entry) => entry.state === "needs-you" || entry.state === "error");
}

/** Tiles in groups: by folder or machine (alphabetical), or by state (needs you first). Tiles keep their order. */
export function groupTiles(tiles: readonly Tile[], by: Grouping): TileGroup[] {
  const groups = new Map<string, TileGroup>();
  for (const entry of tiles) {
    const [key, label] = by === "folder"
      ? [entry.folder ?? "", entry.folder ?? "No folder"]
      : by === "machine"
        ? [entry.machineId, entry.machineName]
        : [entry.state, STATE_LABEL[entry.state]];
    const group = groups.get(key) ?? { key, label, tiles: [] };
    group.tiles.push(entry);
    groups.set(key, group);
  }
  const list = [...groups.values()];
  return by === "state"
    ? list.sort((a, b) => STATE_ORDER.indexOf(a.key as TileState) - STATE_ORDER.indexOf(b.key as TileState))
    : list.sort((a, b) => a.label.localeCompare(b.label));
}

/** How many tiles are working and how many helpers they run, for the line above the wall. */
export function summarize(tiles: readonly Tile[]): { running: number; helpers: number; needYou: number } {
  return {
    running: tiles.filter((entry) => entry.state === "working").length,
    helpers: tiles.reduce((total, entry) => total + entry.helpers, 0),
    needYou: needsYou(tiles).length,
  };
}
