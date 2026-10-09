/**
 * The phone's home screen, as data: everything waiting on you across every machine, then what's working, then what
 * finished in the last day. Pure: the feeds (inbox-feed.ts) fill it in, the inbox page draws it.
 */
import type { SessionListItem } from "@/api/client";
import type { PermissionAsk } from "@/composables/use-session-permissions";
import { isTopLevel, sessionBucket, sessionUpdatedAt } from "@/lib/needs-you";
import type { FeedStatus } from "@/lib/machine-feed";
import type { QuestionInfo } from "@/lib/question-types";
import { askPreviewWording } from "@/lib/tools";

/** What a waiting session waits on, when the phone could find out. */
export type InboxAsk =
  | { kind: "permission"; ask: PermissionAsk }
  | { kind: "question"; requestId: string; question: QuestionInfo; more: number };

/** One machine as the inbox knows it. */
export interface InboxMachineState {
  id: string;
  name: string;
  isHome: boolean;
  /** "linux", "macos", "windows", when the machine says. */
  os?: string | null;
  status: FeedStatus;
  /** When the machine last answered, in ms. */
  lastHeardAt: number | null;
  sessions: SessionListItem[];
  /** Asks by session id. */
  asks: Record<string, InboxAsk>;
  /** Why the phone can't reach it at all (no key, http only), when it can't. */
  problem?: string | null;
}

export interface InboxItem {
  key: string;
  machineId: string;
  machineName: string;
  sessionId: string;
  title: string;
  /** The folder it works in ("weave-fleet"), and its branch, when the machine says. */
  folder: string | null;
  branch: string | null;
  updatedAt: number;
  status: SessionListItem["sessionStatus"];
  activity: string | null;
  ask: InboxAsk | null;
  /** The machine didn't answer last time: this is how it was then. */
  stale: boolean;
}

export interface Inbox {
  needsYou: InboxItem[];
  working: InboxItem[];
  finished: InboxItem[];
}

/** Finished sessions stay on the home screen for a day. */
export const FINISHED_FOR_MS = 24 * 60 * 60_000;

/** The folder a session works in, by name: the project's or workspace's, else the last part of its path. */
export function sessionFolder(session: Pick<SessionListItem, "projectName" | "workspaceDisplayName" | "sourceDirectory" | "workspaceDirectory">): string | null {
  const named = session.projectName?.trim() || session.workspaceDisplayName?.trim();
  if (named) return named;
  const path = (session.sourceDirectory || session.workspaceDirectory || "").replace(/[\\/]+$/, "");
  return path ? path.split(/[\\/]/).pop() || null : null;
}

function item(machine: InboxMachineState, session: SessionListItem): InboxItem {
  return {
    key: `${machine.id}:${session.session.id}`,
    machineId: machine.id,
    machineName: machine.name,
    sessionId: session.session.id,
    title: session.session.title?.trim() || "Untitled session",
    folder: sessionFolder(session),
    branch: session.branch?.trim() || null,
    updatedAt: sessionUpdatedAt(session),
    status: session.sessionStatus,
    activity: session.activityStatus ?? null,
    ask: machine.asks[session.session.id] ?? null,
    stale: machine.status === "unreachable",
  };
}

const newestFirst = (a: InboxItem, b: InboxItem) => b.updatedAt - a.updatedAt;

/** Groups every machine's sessions into the inbox's three sections, newest first. */
export function buildInbox(machines: readonly InboxMachineState[], now: number, finishedFor = FINISHED_FOR_MS): Inbox {
  const needsYou: InboxItem[] = [];
  const working: InboxItem[] = [];
  const finished: InboxItem[] = [];

  for (const machine of machines) {
    for (const session of machine.sessions) {
      if (!isTopLevel(session) || session.retentionStatus === "archived") continue;
      const entry = item(machine, session);
      switch (sessionBucket(session)) {
        case "needs-you":
          needsYou.push(entry);
          break;
        case "working":
          working.push(entry);
          break;
        default:
          if (now - entry.updatedAt <= finishedFor) finished.push(entry);
      }
    }
  }

  return { needsYou: needsYou.sort(newestFirst), working: working.sort(newestFirst), finished: finished.sort(newestFirst) };
}

/** The one-line preview of what a session waits on, for the card. */
export function askPreview(entry: InboxItem): { lead: string; detail: string | null; code: boolean } {
  if (entry.status === "error") return { lead: "Stopped with an error", detail: null, code: false };
  const ask = entry.ask;
  if (!ask) return { lead: "Waiting on you", detail: null, code: false };
  if (ask.kind === "question") return { lead: "Asked:", detail: ask.question.question, code: false };

  return askPreviewWording(ask.ask);
}

/** "hangar", "hangar and falcon", "hangar, falcon and shuttle". */
export function listNames(names: readonly string[]): string {
  if (names.length <= 1) return names[0] ?? "";
  return `${names.slice(0, -1).join(", ")} and ${names[names.length - 1]}`;
}

/** How a machine's dot looks under the page title: online filled, reaching it hollow, unreachable red. */
export type MachineTone = "online" | "connecting" | "away";

/**
 * The line under "Needs you": each machine with its dot, then a word for them all — "online", "falcon unreachable",
 * "1 connecting", or "connecting…" before any has answered.
 */
export function machinesStatus(machines: readonly Pick<InboxMachineState, "name" | "status">[]): { machines: { name: string; tone: MachineTone }[]; note: string } {
  const list = machines.map((m) => ({ name: m.name, tone: (m.status === "live" || m.status === "polling" ? "online" : m.status === "unreachable" ? "away" : "connecting") as MachineTone }));
  const online = list.filter((m) => m.tone === "online").length;
  const away = list.filter((m) => m.tone === "away").map((m) => m.name);
  const connecting = list.length - online - away.length;
  if (online + away.length === 0) return { machines: list, note: "connecting…" };
  const parts: string[] = [];
  if (away.length) parts.push(`${listNames(away)} unreachable`);
  else if (!connecting) parts.push("online");
  if (connecting) parts.push(`${connecting} connecting`);
  return { machines: list, note: parts.join(" · ") };
}

/** The status glyph in front of a session row: amber diamond, working dots, red triangle, or a quiet dot. */
export function rowGlyph(entry: Pick<InboxItem, "status">): "waiting" | "working" | "error" | "quiet" {
  if (entry.status === "waiting_input") return "waiting";
  if (entry.status === "active") return "working";
  if (entry.status === "error") return "error";
  return "quiet";
}

/** The line under a row's title: where it is and its branch — "falcon · refactor/references" in the inbox (by
 * machine), "weave-fleet · fix/flaky-reconnect" under a machine's heading (by folder). */
export function rowPlace(entry: Pick<InboxItem, "machineName" | "folder" | "branch">, by: "machine" | "folder"): string {
  const where = by === "folder" ? entry.folder ?? entry.machineName : entry.machineName;
  return [where, entry.branch].filter(Boolean).join(" · ");
}

/** The right of a row: "Needs you" (amber), "Failed" (red), how long it's been working, or how long ago it finished. */
export function rowMeta(entry: Pick<InboxItem, "status">, words: { duration: string; short: string }): { text: string; tone: "waiting" | "error" | "quiet" } {
  if (entry.status === "waiting_input") return { text: "Needs you", tone: "waiting" };
  if (entry.status === "error") return { text: "Failed", tone: "error" };
  if (entry.status === "active") return { text: words.duration, tone: "quiet" };
  return { text: words.short, tone: "quiet" };
}
