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

export type DreamChangeKind = "added" | "rewritten" | "merged" | "retired" | "kept" | "left" | "skipped";

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
  /** The night that started it; null for Dream now. */
  runId: string | null;
  tokens: number | null;
  since: string | null;
}

/** The live event a dream sends when it ends; its payload is a DreamView. */
export const MEMORY_DREAMED = "memory.dreamed";

export interface NightlyDreamingView {
  enabled: boolean;
  automationId: string | null;
  paused: boolean;
  time: string;
  timeZone: string | null;
  harnessType: string | null;
  /** `provider/model`. */
  model: string | null;
  budgetTokens: number;
  nextRunAt: string | null;
  /** Dreams from a night that finished in the last 24 hours, newest first. */
  recent: DreamView[];
}

export interface NightlyDreamingChange {
  enabled?: boolean;
  time?: string;
  timeZone?: string;
  harnessType?: string | null;
  model?: string | null;
  budgetTokens?: number;
}

export const DREAM_BUDGETS = [100_000, 300_000, 500_000, 1_000_000] as const;
export const NIGHTLY_HARNESSES = ["opencode2", "claude-code"] as const;

/** 186000 reads "186k", 1000000 reads "1M". */
export function formatTokens(tokens: number): string {
  if (tokens >= 1_000_000) return `${+(tokens / 1_000_000).toFixed(1)}M`;
  return tokens >= 1000 ? `${Math.round(tokens / 1000)}k` : String(tokens);
}

/** Changes a dream made that are still in place. */
export const appliedDreamChanges = (dream: DreamView): DreamChangeView[] =>
  groupDreamChanges(dream.changes).applied.filter((change) => !change.undone);

export function describeDreamChanges(dream: DreamView): string {
  const counts = new Map<DreamChangeKind, number>();
  for (const change of appliedDreamChanges(dream)) counts.set(change.kind, (counts.get(change.kind) ?? 0) + 1);
  const words: Record<string, string> = { added: "added", rewritten: "rewritten", merged: "merged", retired: "retired", kept: "kept for good" };
  const parts = [...counts].map(([kind, count]) => `${count} ${words[kind]}`);
  const sessions = dream.readSessions.length;
  const read = `${sessions} ${sessions === 1 ? "session" : "sessions"} read overnight`;
  return counts.size > 0 ? `${read} · ${parts.join(", ")}` : read;
}

export const isDreamBusy = (dream: DreamView | null | undefined): boolean => dream?.status === "reading" || dream?.status === "running";

const KIND_LABELS: Record<DreamChangeKind, string> = {
  added: "Added",
  rewritten: "Rewritten",
  merged: "Merged",
  retired: "Retired",
  kept: "Kept for good",
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

const NIGHTLY_PATH = "/api/memory/dreaming/nightly";

export const getNightlyDreaming = (): Promise<NightlyDreamingView> => send(NIGHTLY_PATH, "Couldn't load nightly dreaming.");

export function setNightlyDreaming(change: NightlyDreamingChange): Promise<NightlyDreamingView> {
  return send(NIGHTLY_PATH, "Couldn't save nightly dreaming.", { method: "PUT", body: JSON.stringify(change) });
}
