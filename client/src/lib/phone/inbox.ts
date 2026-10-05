/**
 * The phone's home screen, as data: everything waiting on you across every machine, then what's working, then what
 * finished in the last day. Pure: the feeds (machine-feed.ts) fill it in, the inbox page draws it.
 */
import type { SessionListItem } from "@/api/client";
import type { PermissionAsk } from "@/composables/use-session-permissions";
import { isTopLevel, sessionBucket, sessionUpdatedAt } from "@/lib/needs-you";
import type { QuestionInfo } from "@/lib/question-types";

/** How a machine's feed is getting news: live over its event hub, by polling, or not at all. */
export type FeedStatus = "connecting" | "live" | "polling" | "unreachable";

/** What a waiting session waits on, when the phone could find out. */
export type InboxAsk =
  | { kind: "permission"; ask: PermissionAsk }
  | { kind: "question"; requestId: string; question: QuestionInfo; more: number };

/** One machine as the inbox knows it. */
export interface InboxMachineState {
  id: string;
  name: string;
  isHome: boolean;
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

function item(machine: InboxMachineState, session: SessionListItem): InboxItem {
  return {
    key: `${machine.id}:${session.session.id}`,
    machineId: machine.id,
    machineName: machine.name,
    sessionId: session.session.id,
    title: session.session.title?.trim() || "Untitled session",
    updatedAt: sessionUpdatedAt(session),
    status: session.sessionStatus,
    activity: session.activityStatus ?? null,
    ask: machine.asks[session.session.id] ?? null,
    stale: machine.status === "unreachable",
  };
}

const newestFirst = (a: InboxItem, b: InboxItem) => b.updatedAt - a.updatedAt;

/** Groups every machine's sessions into the inbox's three sections, newest first. */
export function buildInbox(machines: readonly InboxMachineState[], now: number): Inbox {
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
          if (now - entry.updatedAt <= FINISHED_FOR_MS) finished.push(entry);
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

  const what = ask.ask.title || ask.ask.tool;
  switch (ask.ask.kind) {
    case "shell":
      return { lead: "Wants to run", detail: what, code: true };
    case "edit":
      return { lead: "Wants to edit", detail: what, code: true };
    case "read":
      return { lead: "Wants to read", detail: what, code: true };
    case "web":
      return { lead: "Wants to open", detail: what, code: true };
    default:
      return { lead: `Wants to use ${ask.ask.tool}`, detail: ask.ask.title ?? null, code: !!ask.ask.title };
  }
}

/** "hangar · Working · 6m", the line under a working or finished row. */
export function rowInfo(entry: InboxItem, age: string): string {
  if (entry.status === "active") {
    const what = entry.activity === "retry" ? "Retrying" : entry.activity === "delegating" ? "Delegating" : "Working";
    return `${entry.machineName} · ${entry.stale ? `${what} when last heard` : what} · ${age}`;
  }
  return `${entry.machineName} · ${age} ago`;
}
