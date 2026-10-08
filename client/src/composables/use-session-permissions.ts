import { computed, onBeforeUnmount, reactive, toValue, watch, type MaybeRefOrGetter } from "vue";
import { onReconnect, useWeaveSocket } from "@/composables/use-weave-socket";
import { useMachineTarget } from "@/lib/machine-target";

/** What an ask is for, whatever the harness calls the tool. */
export type PermissionKind = "read" | "edit" | "shell" | "web" | "other";

/** How the user answers an ask. */
export type PermissionReply = "once" | "always" | "reject";

/** An agent's request to do something the session's permission level doesn't allow (Fleet's `PermissionAsk`). */
export interface PermissionAsk {
  id: string;
  /** The session whose harness asked, which takes the answer: a subagent's ask shows on the session it works for. */
  sessionId: string;
  kind: PermissionKind;
  /** The harness's name for the tool, e.g. `bash`, `edit`, `WebFetch`. */
  tool: string;
  /** What it touches, in one line: the command, the file, the address. */
  title?: string | null;
  /** More to look at before answering: a diff, a whole script. */
  detail?: string | null;
  directory?: string | null;
  /** What "Don't ask again" would allow, e.g. `git push *`; empty when it's the whole tool. */
  always: string[];
  callId?: string | null;
  /** Set when a subagent asked rather than the session's own agent. */
  subagent?: string | null;
  askedAt: string;
}

const ASKED_EVENT = "permission.asked" as const;
const REPLIED_EVENT = "permission.replied" as const;
const KINDS: readonly PermissionKind[] = ["read", "edit", "shell", "web", "other"];

/** Each session's waiting asks as Fleet last said, so switching back to a session shows them at once. */
const asksBySession = reactive<Record<string, PermissionAsk[]>>({});

function toStrings(value: unknown): string[] {
  return Array.isArray(value) ? value.filter((item): item is string => typeof item === "string" && item.length > 0) : [];
}

function optionalString(value: unknown): string | null {
  return typeof value === "string" && value.length > 0 ? value : null;
}

export function toPermissionAsk(value: unknown): PermissionAsk | null {
  if (!value || typeof value !== "object") return null;
  const raw = value as Record<string, unknown>;
  if (typeof raw.id !== "string" || typeof raw.sessionId !== "string" || typeof raw.tool !== "string") return null;
  const kind = KINDS.includes(raw.kind as PermissionKind) ? (raw.kind as PermissionKind) : "other";
  return {
    id: raw.id,
    sessionId: raw.sessionId,
    kind,
    tool: raw.tool,
    title: optionalString(raw.title),
    detail: optionalString(raw.detail),
    directory: optionalString(raw.directory),
    always: toStrings(raw.always),
    callId: optionalString(raw.callId),
    subagent: optionalString(raw.subagent),
    askedAt: typeof raw.askedAt === "string" ? raw.askedAt : new Date().toISOString(),
  };
}

function withAsk(asks: readonly PermissionAsk[], ask: PermissionAsk): PermissionAsk[] {
  return asks.some((existing) => existing.id === ask.id) ? [...asks] : [...asks, ask];
}

function withoutAsk(asks: readonly PermissionAsk[] | undefined, id: string): PermissionAsk[] {
  return (asks ?? []).filter((ask) => ask.id !== id);
}

/** Fleet's own error text from a failed answer, or a plain fallback. */
function errorMessage(body: unknown, status: number): string {
  if (body && typeof body === "object") {
    for (const key of ["error", "detail", "title"]) {
      const value = (body as Record<string, unknown>)[key];
      if (typeof value === "string" && value.trim().length > 0) return value;
    }
  }
  return `The answer didn't reach the agent (HTTP ${status}).`;
}

/**
 * The agent's asks waiting on the user in a session: what it wants to do that the session's permission level doesn't
 * allow. Fleet keeps them, so they load when the session opens and after a reconnect, and `permission.asked` and
 * `permission.replied` on the session's topic keep them current. A subagent's asks show on the session it works for.
 */
export function useSessionPermissions(sessionId: MaybeRefOrGetter<string>) {
  const { api } = useMachineTarget();
  const { subscribeV2 } = useWeaveSocket();
  const asks = computed<readonly PermissionAsk[]>(() => asksBySession[toValue(sessionId)] ?? []);
  let loadId = 0;

  async function load(id: string): Promise<void> {
    const current = ++loadId;
    try {
      const { data, response } = await api.GET("/api/sessions/{id}/permissions", { params: { path: { id } } });
      if (current !== loadId || !response.ok) return;
      asksBySession[id] = (Array.isArray(data) ? data : [])
        .map(toPermissionAsk)
        .filter((ask): ask is PermissionAsk => ask !== null);
    } catch (loadError) {
      console.warn(`Failed to load the permission asks of session ${id}:`, loadError);
    }
  }

  const stopWatching = watch(
    () => toValue(sessionId),
    (id, _previous, onCleanup) => {
      if (!id) return;
      const unsubscribe = subscribeV2(
        `session:${id}`,
        () => {
          // Asks load from their own endpoint; snapshots don't carry them.
        },
        (event) => {
          if (event.type === ASKED_EVENT) {
            const ask = toPermissionAsk(event.payload);
            if (!ask) return;
            loadId += 1;
            asksBySession[id] = withAsk(asksBySession[id] ?? [], ask);
          } else if (event.type === REPLIED_EVENT) {
            const askId = event.payload?.id;
            if (typeof askId !== "string") return;
            loadId += 1;
            asksBySession[id] = withoutAsk(asksBySession[id], askId);
          }
        },
      );
      void load(id);
      const stopReconnect = onReconnect(() => void load(id));
      onCleanup(() => {
        unsubscribe();
        stopReconnect();
      });
    },
    { immediate: true },
  );

  onBeforeUnmount(stopWatching);

  /**
   * Answers `ask`. The card goes at once; an ask that no longer waits (its turn ended) goes too. Throws with Fleet's
   * reason when the answer didn't reach the agent, and the card stays.
   */
  async function answer(ask: PermissionAsk, reply: PermissionReply, message?: string): Promise<void> {
    const { error, response } = await api.POST("/api/sessions/{id}/permissions/{requestId}", {
      params: { path: { id: ask.sessionId, requestId: ask.id } },
      body: { reply, message: message?.trim() ? message.trim() : null },
    });
    if (response.ok || response.status === 404) {
      for (const id of Object.keys(asksBySession)) {
        asksBySession[id] = withoutAsk(asksBySession[id], ask.id);
      }
      return;
    }
    throw new Error(errorMessage(error, response.status));
  }

  return { asks, answer };
}
