import { send } from "@/lib/agent-memory";

/** The server's preference for Dreaming (Settings → Features): off, Memory shows nothing new and no request is made. */
export const DREAMING_PREFERENCE_KEY = "Dreaming";

/** How often a dream that's reading or running is asked about. */
export const DREAM_POLL_MS = 3000;

export interface DreamSessionRef {
  id: string;
  title: string;
  harnessType: string | null;
}

export type DreamChangeKind = "added" | "rewritten" | "merged" | "retired" | "left" | "skipped";

export interface DreamChangeView {
  id: string;
  kind: DreamChangeKind;
  list: "repository" | "machine" | null;
  noteId: string | null;
  text: string | null;
  replaced: { id: string; text: string; kind: string; list: string }[];
  why: string;
  sessions: DreamSessionRef[];
  ofSessions: number;
  quote: string | null;
  reason: string | null;
  undone: boolean;
}

export type DreamStatus = "reading" | "running" | "done" | "failed";

export interface DreamView {
  id: string;
  repository: string;
  repositoryName: string;
  sessionId: string | null;
  harnessType: string | null;
  status: DreamStatus;
  error: string | null;
  started: string;
  finished: string | null;
  readSessions: DreamSessionRef[];
  changes: DreamChangeView[];
}

export const isDreamBusy = (dream: DreamView | null | undefined): boolean => dream?.status === "reading" || dream?.status === "running";

const KIND_LABELS: Record<DreamChangeKind, string> = {
  added: "Added",
  rewritten: "Rewritten",
  merged: "Merged",
  retired: "Retired",
  left: "Left as is",
  skipped: "Not changed",
};

export const dreamKindLabel = (kind: DreamChangeKind): string => KIND_LABELS[kind];

export const dreamListLabel = (list: string | null): string => (list === "machine" ? "This machine" : "Repository");

/** Applied changes first, then what it left as is, then what it saw in one session only. */
export function groupDreamChanges(changes: readonly DreamChangeView[]) {
  return {
    applied: changes.filter((change) => change.kind !== "left" && change.kind !== "skipped"),
    left: changes.filter((change) => change.kind === "left"),
    skipped: changes.filter((change) => change.kind === "skipped"),
  };
}

const DREAMS_PATH = "/api/memory/dreams";

export async function listDreams(repository: string): Promise<DreamView[]> {
  const body = await send<{ dreams: DreamView[] }>(`${DREAMS_PATH}?repository=${encodeURIComponent(repository)}`, "Couldn't load the dreams.");
  return body.dreams;
}

export function startDream(repository: string, choice: { harnessType?: string; providerId?: string; modelId?: string } = {}): Promise<DreamView> {
  return send(DREAMS_PATH, "Couldn't start the dream.", { method: "POST", body: JSON.stringify({ repository, ...choice }) });
}

export function undoDreamChange(dreamId: string, changeId: string): Promise<DreamView> {
  return send(
    `${DREAMS_PATH}/${encodeURIComponent(dreamId)}/changes/${encodeURIComponent(changeId)}/undo`,
    "Couldn't undo the change.",
    { method: "POST" },
  );
}
