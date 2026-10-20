/**
 * Loads a session's whole history for an export. The conversation only holds the pages it has scrolled to, so this
 * pages `GET /api/sessions/{id}/messages` back to the first message, newest page first.
 */
import type { AccumulatedMessage } from "@/lib/client-types";
import { apiFetch } from "@/lib/api-client";
import { convertFleetMessageToAccumulated, type FleetMessage } from "@/lib/pagination-utils";

const PAGE_SIZE = 200;
/** A history longer than this is a loop, not a conversation. */
const MAX_PAGES = 500;

interface MessagesResponse {
  messages?: FleetMessage[];
  pagination?: { hasMore?: boolean; oldestMessageId?: string | null };
}

export class ExportHistoryError extends Error {}

/**
 * Every message of the session, oldest first. `onProgress` hears the count after each page. A page that fails stops
 * the load with an `ExportHistoryError` naming what failed, so a file is never made from half a history.
 */
export async function loadWholeHistory(
  sessionId: string,
  onProgress?: (loaded: number) => void,
  signal?: AbortSignal,
): Promise<AccumulatedMessage[]> {
  let all: AccumulatedMessage[] = [];
  let before: string | null = null;

  for (let page = 0; page < MAX_PAGES; page++) {
    const query = new URLSearchParams({ limit: String(PAGE_SIZE) });
    if (before) query.set("before", before);

    let response: Response;
    try {
      response = await apiFetch(`/api/sessions/${encodeURIComponent(sessionId)}/messages?${query}`, { signal });
    } catch (error) {
      if (signal?.aborted) throw error;
      throw new ExportHistoryError(`Could not reach Fleet to load the conversation (after ${all.length} messages).`);
    }
    if (!response.ok) {
      throw new ExportHistoryError(`Fleet could not load the conversation (error ${response.status} after ${all.length} messages).`);
    }

    let body: MessagesResponse;
    try {
      body = await response.json() as MessagesResponse;
    } catch {
      throw new ExportHistoryError(`Fleet sent a page of the conversation that could not be read (after ${all.length} messages).`);
    }

    const older = (body.messages ?? []).map((message) => ({ ...convertFleetMessageToAccumulated(message), sessionId }));
    const known = new Set(all.map((message) => message.messageId));
    all = [...older.filter((message) => !known.has(message.messageId)), ...all];
    onProgress?.(all.length);

    const oldest = body.pagination?.oldestMessageId ?? null;
    if (!body.pagination?.hasMore) return all;
    // A cursor that doesn't move would ask for the same page forever.
    if (!oldest || oldest === before) {
      throw new ExportHistoryError(`Fleet did not say where the older messages start (after ${all.length} messages).`);
    }
    before = oldest;
  }

  throw new ExportHistoryError("The conversation is longer than Fleet can export.");
}
