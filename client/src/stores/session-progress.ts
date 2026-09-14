import { defineStore } from "pinia";
import { shallowRef } from "vue";
import { apiFetch } from "@/lib/api-client";
import { parseProgressDetail, sameProgressSummary, type SessionProgressDetail, type SessionProgressSummary } from "@/lib/session-progress";

const path = (sessionId: string) => `/api/sessions/${encodeURIComponent(sessionId)}/progress`;

/**
 * The full progress (todo list and counts) of sessions that have been opened. Session rows read their
 * summary from the sessions store instead, so this only holds what an open session needs.
 */
export const useSessionProgressStore = defineStore("session-progress", () => {
  /** null means the server has no progress for the session. */
  const bySession = shallowRef<Record<string, SessionProgressDetail | null>>({});
  const loading = new Map<string, Promise<void>>();

  function set(sessionId: string, detail: SessionProgressDetail | null): void {
    bySession.value = { ...bySession.value, [sessionId]: detail };
  }

  /** Applies progress from the API or a push, unless what's held is newer. */
  function apply(detail: SessionProgressDetail): void {
    const existing = bySession.value[detail.sessionId];
    if (existing && existing.updatedAt > detail.updatedAt) return;
    set(detail.sessionId, detail);
  }

  /** Forgets a session's detail when a pushed row summary shows it's out of date, so it's fetched again on view. */
  function noteSummary(summary: SessionProgressSummary): void {
    const existing = bySession.value[summary.sessionId];
    if (existing === undefined || sameProgressSummary(existing, summary)) return;
    bySession.value = Object.fromEntries(
      Object.entries(bySession.value).filter(([sessionId]) => sessionId !== summary.sessionId),
    );
  }

  /** Loads a session's progress once (or again with `force`); pushes keep it current afterwards. */
  function ensureLoaded(sessionId: string, options: { force?: boolean } = {}): Promise<void> {
    if (!sessionId || (!options.force && sessionId in bySession.value)) return Promise.resolve();
    const pending = loading.get(sessionId);
    if (pending) return pending;

    const request = (async () => {
      try {
        const response = await apiFetch(path(sessionId));
        if (response.status === 204) {
          if (!bySession.value[sessionId]) set(sessionId, null);
          return;
        }
        if (!response.ok) return;
        const detail = parseProgressDetail(await response.json());
        if (detail) apply(detail);
      } catch {
        // Progress is extra information; the session works without it.
      } finally {
        loading.delete(sessionId);
      }
    })();
    loading.set(sessionId, request);
    return request;
  }

  function progressFor(sessionId: string): SessionProgressDetail | null {
    return bySession.value[sessionId] ?? null;
  }

  return { bySession, apply, noteSummary, ensureLoaded, progressFor };
});
