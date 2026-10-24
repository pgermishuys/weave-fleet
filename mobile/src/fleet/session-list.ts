// The sessions list across machines: keeping each machine's list live from its `sessions` topic, and sectioning it.
import type { SessionListItem } from "@fleet/api/client";
import type { DomainEvent } from "@fleet/lib/domain-events";
import { isTopLevel, sessionBucket, sessionUpdatedAt, type SessionBucket } from "@fleet/lib/needs-you";
import { deriveSessionStatus } from "@fleet/lib/session-status";
import { short } from "@fleet/lib/phone/time";

/** The wire calls a hub event's payload "properties" (same as the web client's toDomainEvent). */
export function toDomainEvent(eventId: number | null, data: unknown): DomainEvent {
  const wire = data as { type: string; properties?: unknown };
  return { type: wire.type, payload: wire.properties, ...(eventId !== null ? { eventId } : {}) } as DomainEvent;
}

/** What a `sessions` topic event does to one machine's list: a changed list, and/or "fetch it again". */
export function applySessionsEvent(list: SessionListItem[], event: DomainEvent): { list: SessionListItem[]; refetch: boolean } {
  const type = event.type as string;
  const payload = event.payload as { sessionId?: string; activityStatus?: string; attempt?: number | null; maxAttempts?: number | null; message?: string | null; next?: string | null };
  if (type === "activity_status" && payload.sessionId && payload.activityStatus) {
    const retrying = payload.activityStatus === "retry";
    return {
      refetch: !list.some((item) => item.session.id === payload.sessionId),
      list: list.map((item) => item.session.id !== payload.sessionId ? item : {
        ...item,
        activityStatus: payload.activityStatus,
        sessionStatus: deriveSessionStatus(payload.activityStatus!, item.sessionStatus),
        retryAttempt: retrying ? payload.attempt ?? null : null,
        retryMaxAttempts: retrying ? payload.maxAttempts ?? null : null,
        retryMessage: retrying ? payload.message ?? null : null,
        retryNext: retrying ? payload.next ?? null : null,
      }),
    };
  }
  // A page was shown, reopened or closed: the row's "N new pages" reads these.
  if (type === "session.pages" && payload.sessionId) {
    const pages = event.payload as { pageCount?: number; lastPageShownAt?: string | null };
    return {
      refetch: !list.some((item) => item.session.id === payload.sessionId),
      list: list.map((item) => item.session.id !== payload.sessionId ? item : {
        ...item, pageCount: pages.pageCount ?? 0, lastPageShownAt: pages.lastPageShownAt ?? null,
      }),
    };
  }
  if (type === "session_archived" || type === "session_deleted") {
    return { list: list.filter((item) => item.session.id !== payload.sessionId), refetch: false };
  }
  // A new session, a restored one, or a turn that ended (titles and times change then): the server has the answer.
  if (type === "session_created" || type === "session_unarchived" || type === "session.idled" || type === "turn.ended") {
    return { list, refetch: true };
  }
  return { list, refetch: false };
}

export interface SessionEntry {
  key: string;
  machineId: string;
  machineName: string;
  item: SessionListItem;
  bucket: SessionBucket;
}

export interface SessionSection {
  key: SessionBucket;
  label: string;
  data: SessionEntry[];
}

const LABELS: Record<SessionBucket, string> = { "needs-you": "Needs you", working: "Working", recent: "Recent" };
const RECENT_LIMIT = 30;

/** Top-level, unarchived sessions from every machine, newest first within "Needs you", "Working" and "Recent". */
export function sectionSessions(lists: ReadonlyArray<{ machineId: string; machineName: string; items: readonly SessionListItem[] }>): SessionSection[] {
  const buckets: Record<SessionBucket, SessionEntry[]> = { "needs-you": [], working: [], recent: [] };
  for (const { machineId, machineName, items } of lists) {
    for (const item of items) {
      if (!isTopLevel(item) || item.archivedAt) continue;
      const bucket = sessionBucket(item);
      buckets[bucket].push({ key: `${machineId}:${item.session.id}`, machineId, machineName, item, bucket });
    }
  }
  return (["needs-you", "working", "recent"] as const)
    .map((key) => ({
      key,
      label: LABELS[key],
      data: buckets[key].sort((a, b) => sessionUpdatedAt(b.item) - sessionUpdatedAt(a.item)).slice(0, key === "recent" ? RECENT_LIMIT : undefined),
    }))
    .filter((section) => section.data.length > 0);
}

/** "pay-app · feat/refund": where the session works. */
export function rowDetail(item: SessionListItem): string {
  const folder = item.workspaceDisplayName || item.workspaceDirectory.split(/[\\/]/).filter(Boolean).pop() || "";
  return [folder, item.branch].filter(Boolean).join(" · ");
}

export const rowAge = (item: SessionListItem, now: number) => short(sessionUpdatedAt(item), now);
