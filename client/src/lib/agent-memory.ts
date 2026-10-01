import { apiFetch } from "@/lib/api-client";
import { extractApiError } from "@/lib/api-error";

/**
 * Agent memory: notes agents keep about a repository and about this machine, so new sessions start out knowing what
 * earlier ones learned. Off until the user turns it on in Settings → Memory.
 */

/** The event on the "sessions" topic when an agent saves a note. */
export const MEMORY_SAVED = "memory.saved";

/** How long the notice for a saved note offers Undo. */
export const MEMORY_UNDO_MS = 6000;

export type MemoryListName = "repository" | "machine";

/** learned: the agent found it out. from-you: the user asked, corrected or stated a preference. added: written in Settings. */
export type MemoryKind = "learned" | "from-you" | "added";

export interface MemoryNote {
  id: string;
  list: MemoryListName;
  text: string;
  kind: MemoryKind | string;
  repository: string | null;
  sessionId: string | null;
  sessionTitle: string | null;
  created: string;
  updated: string;
  /** Days of use the note lasts; none when it never expires (notes from you, and learned notes you kept). */
  lifetime?: number | null;
  /** Days of use left before sessions stop reading it. */
  daysLeft?: number | null;
  /** It had its days: sessions don't read it, and the same lesson learned again brings it back. */
  expired?: boolean;
  /** How many times an agent learned it again after it expired. */
  relearned?: number;
}

export interface MemoryRepository {
  path: string;
  name: string;
  count: number;
}

export interface MemoryOverview {
  enabled: boolean;
  repositories: MemoryRepository[];
  machineCount: number;
  maxRepositoryNotes: number;
  maxMachineNotes: number;
}

export interface MemoryNotes {
  repository: string | null;
  repositoryNotes: MemoryNote[];
  machineNotes: MemoryNote[];
  /** Roughly what the notes add to each request a session sends. */
  tokens: number;
  /** Learned notes that expired, both lists. */
  expiredNotes?: MemoryNote[];
}

export interface MemorySavedPayload {
  note: MemoryNote;
  repositoryName: string | null;
  /** The note this one replaced, as it was, so Undo can put it back. */
  previous?: MemoryNote | null;
}

const MEMORY_PATH = "/api/memory";

async function send<T>(path: string, fallback: string, init?: RequestInit): Promise<T> {
  const response = await apiFetch(path, init && {
    ...init,
    headers: { "Content-Type": "application/json", ...init.headers },
  });
  if (!response.ok) {
    let message = fallback;
    try {
      const body = (await response.json()) as { error?: unknown };
      message = extractApiError(body.error ?? body, fallback);
    } catch {
      // Not JSON: the fallback says what failed.
    }
    throw new Error(message);
  }
  const text = await response.text();
  return (text ? JSON.parse(text) : undefined) as T;
}

export function getMemory(): Promise<MemoryOverview> {
  return send(MEMORY_PATH, "Couldn't load memory.");
}

export function setMemoryEnabled(enabled: boolean): Promise<MemoryOverview> {
  return send(MEMORY_PATH, `Couldn't turn memory ${enabled ? "on" : "off"}.`, { method: "PUT", body: JSON.stringify({ enabled }) });
}

export function listMemoryNotes(repository: string | null): Promise<MemoryNotes> {
  const query = repository ? `?repository=${encodeURIComponent(repository)}` : "";
  return send(`${MEMORY_PATH}/notes${query}`, "Couldn't load the notes.");
}

export function addMemoryNote(list: MemoryListName, repository: string | null, text: string): Promise<MemoryNote> {
  return send(`${MEMORY_PATH}/notes`, "Couldn't add the note.", { method: "POST", body: JSON.stringify({ list, repository, text }) });
}

export function updateMemoryNote(id: string, text: string): Promise<MemoryNote> {
  return send(`${MEMORY_PATH}/notes/${encodeURIComponent(id)}`, "Couldn't save the note.", { method: "PUT", body: JSON.stringify({ text }) });
}

/** A learned note that should never expire, or an expired one brought back for good. */
export function keepMemoryNote(id: string): Promise<MemoryNote> {
  return send(`${MEMORY_PATH}/notes/${encodeURIComponent(id)}/keep`, "Couldn't keep the note.", { method: "POST" });
}

export function forgetMemoryNote(id: string): Promise<void> {
  return send(`${MEMORY_PATH}/notes/${encodeURIComponent(id)}`, "Couldn't forget the note.", { method: "DELETE" });
}

export function clearMemory(scope: "all" | "machine" | "repository", repository: string | null = null): Promise<{ deleted: number }> {
  return send(`${MEMORY_PATH}/clear`, "Couldn't clear the notes.", { method: "POST", body: JSON.stringify({ scope, repository }) });
}

/** Where a note is kept, as the notice and the conversation say it. */
export function memoryPlace(note: Pick<MemoryNote, "list">, repositoryName: string | null): string {
  return note.list === "machine" ? "this machine" : (repositoryName ?? "this repository");
}

/** How long a learned note has left, as Settings says it; null for a note from the user, which never expires. */
export function memoryLifeLabel(note: Pick<MemoryNote, "kind" | "lifetime" | "daysLeft" | "expired" | "relearned">): string | null {
  const again = note.relearned ? ` · learned ${note.relearned + 1}×` : "";
  if (note.lifetime == null) return note.kind === "learned" ? `Kept for good${again}` : null;
  if (note.expired) return `Expired after ${note.lifetime} days of use${again}`;
  const left = note.daysLeft ?? note.lifetime;
  if (left === 0) return `Last day of use${again}`;
  return `${left} more ${left === 1 ? "day" : "days"} of use${again}`;
}

/** How Settings labels where a note came from. */
export function memoryKindLabel(kind: string): string {
  if (kind === "from-you") return "From you";
  if (kind === "added") return "Added by you";
  return "Learned";
}
