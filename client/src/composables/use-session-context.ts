import { computed, reactive, shallowRef, toValue, type ComputedRef, type MaybeRefOrGetter, type Ref } from "vue";
import { api } from "@/api/client";
import type { SessionContextUsage } from "@/lib/context-usage";

/**
 * How full each open session's context window is, for the ring by Send. The session's stream is the source (its
 * snapshot's `context` and its `context.updated` events, already reduced): it publishes here, so the composer reads
 * it without a subscription of its own.
 */
const contextBySession = reactive(new Map<string, SessionContextUsage | null>());

/** The stream's say on a session's context. Called by `use-session-stream.ts` whenever its state's `context` changes. */
export function publishSessionContext(sessionId: string, context: SessionContextUsage | null): void {
  if (contextBySession.get(sessionId) === context) return;
  contextBySession.set(sessionId, context);
}

export function _resetSessionContextForTesting(): void {
  contextBySession.clear();
}

/** The reason in an error body: Fleet's `{ error }`, or a problem's `detail` or `title`. */
function errorMessage(body: unknown, fallback: string): string {
  if (body && typeof body === "object") {
    for (const key of ["error", "detail", "title"]) {
      const value = (body as Record<string, unknown>)[key];
      if (typeof value === "string" && value.trim().length > 0) return value;
    }
  }
  return fallback;
}

export type CompactResult = { ok: true } | { ok: false; error: string };

/**
 * Compact now. Fleet answers once the harness has taken the request; the compaction's start and end arrive as
 * `context.updated`. Refused during a turn, and for a harness that can't compact.
 */
export async function compactSession(sessionId: string): Promise<CompactResult> {
  try {
    const { error, response } = await api.POST("/api/sessions/{id}/compact", { params: { path: { id: sessionId } } });
    return response.ok ? { ok: true } : { ok: false, error: errorMessage(error, `It couldn't be compacted (HTTP ${response.status}).`) };
  } catch (compactError) {
    return { ok: false, error: compactError instanceof Error ? compactError.message : "It couldn't be compacted." };
  }
}

export interface UseSessionContextResult {
  /** Null until the session's harness has reported a model call. */
  context: ComputedRef<SessionContextUsage | null>;
  /** A Compact now Fleet hasn't answered yet. */
  isRequesting: Ref<boolean>;
  /** Why the last Compact now was refused, until the next one. */
  requestError: Ref<string | null>;
  compact: () => Promise<void>;
}

export function useSessionContext(sessionId: MaybeRefOrGetter<string>): UseSessionContextResult {
  const isRequesting = shallowRef(false);
  const requestError = shallowRef<string | null>(null);

  async function compact(): Promise<void> {
    if (isRequesting.value) return;
    isRequesting.value = true;
    requestError.value = null;
    const result = await compactSession(toValue(sessionId));
    isRequesting.value = false;
    if (!result.ok) requestError.value = result.error;
  }

  return {
    context: computed(() => contextBySession.get(toValue(sessionId)) ?? null),
    isRequesting,
    requestError,
    compact,
  };
}
