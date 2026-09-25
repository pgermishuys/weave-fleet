import { computed, reactive, toValue, watch, type MaybeRefOrGetter } from "vue";
import { api } from "@/api/client";
import type { components } from "@/api/generated/schema";
import { showSentPrompt } from "@/composables/use-send-prompt";

/** A session's side conversation (`/btw`): a hidden session of its own, shown after `boundaryMessageId`. */
export type SideConversation = components["schemas"]["SideConversationResponse"];

/** The agent, model and effort a side question is asked with; none means the session's own. */
export interface SideQuestionChoice {
  agent?: string;
  model?: { providerID: string; modelID: string };
  effort?: string;
}

interface SideConversationState {
  side: SideConversation | null;
  loaded: boolean;
  /** A question is on its way; before the first one's answer, the side conversation is being forked. */
  asking: boolean;
  /** The question that started the side conversation, shown while the fork is made. */
  starting: string | null;
  error?: string;
}

/**
 * Per session, shared by the panel and the composer, and kept while the user is on other sessions. The server has
 * the side conversation (it survives a reload); this only mirrors it.
 */
const states = reactive<Record<string, SideConversationState>>({});

function stateOf(sessionId: string): SideConversationState {
  states[sessionId] ??= { side: null, loaded: false, asking: false, starting: null };
  return states[sessionId];
}

/** The reason in an error body: Fleet's `{ error }`, or a problem's `detail` or `title`. */
function errorMessage(body: unknown): string | undefined {
  if (typeof body === "string" && body.trim().length > 0) return body.trim();
  if (!body || typeof body !== "object") return undefined;

  const record = body as Record<string, unknown>;
  for (const key of ["error", "detail", "title"]) {
    const value = record[key];
    if (typeof value === "string" && value.trim().length > 0) return value;
  }
  return undefined;
}

async function load(sessionId: string): Promise<void> {
  const state = stateOf(sessionId);
  try {
    const { data, response } = await api.GET("/api/sessions/{id}/side", { params: { path: { id: sessionId } } });
    // A question asked while this was loading already knows better.
    if (!state.asking) {
      state.side = response.status === 200 && data ? data : null;
    }
  } catch {
    // The panel stays closed; the next visit asks again.
    return;
  }
  state.loaded = true;
}

/** Forgets what's kept for every session: for tests. */
export function _resetSideConversationsForTesting(): void {
  for (const key of Object.keys(states)) delete states[key];
}

export function useSideConversation(sessionId: MaybeRefOrGetter<string>) {
  const id = computed(() => toValue(sessionId));
  const state = computed(() => stateOf(id.value));

  watch(id, (next) => {
    if (next && !stateOf(next).loaded) void load(next);
  }, { immediate: true });

  const side = computed(() => state.value.side);
  /** The panel shows: a side conversation is open, or the first question is making one. */
  const isOpen = computed(() => state.value.side !== null || state.value.starting !== null);

  /**
   * Asks `question` in the side conversation, starting one when there's none. The question shows in the panel at once;
   * a question the server refuses sets `error` and returns false.
   */
  async function ask(question: string, choice: SideQuestionChoice = {}): Promise<boolean> {
    const parentId = id.value;
    const current = stateOf(parentId);
    const text = question.trim();
    if (!text || current.asking) return false;

    current.asking = true;
    current.error = undefined;
    if (!current.side) current.starting = text;
    const correlationId = `side-${crypto.randomUUID().replaceAll("-", "")}`;
    try {
      const { data, error, response } = await api.POST("/api/sessions/{id}/side", {
        params: { path: { id: parentId } },
        body: {
          text,
          agent: choice.agent ?? null,
          model: choice.model ?? null,
          effort: choice.effort ?? null,
          correlationId,
        } as components["schemas"]["SideQuestionApiRequest"],
      });
      if (!response.ok || !data) {
        current.error = errorMessage(error) ?? `The side question couldn't be asked (HTTP ${response.status}).`;
        return false;
      }

      current.side = data.sideConversation;
      current.loaded = true;
      if (data.messageId) {
        showSentPrompt(data.sideConversation.sessionId, { id: data.messageId, correlationId: data.correlationId, body: text });
      }
      return true;
    } catch (caught) {
      current.error = caught instanceof Error ? caught.message : "The side question couldn't be asked.";
      return false;
    } finally {
      current.asking = false;
      current.starting = null;
    }
  }

  /** Closes the side conversation: the panel goes at once, and the server deletes the fork. */
  async function close(): Promise<void> {
    const parentId = id.value;
    const current = stateOf(parentId);
    const closing = current.side;
    current.side = null;
    current.error = undefined;
    if (!closing) return;

    try {
      const { error, response } = await api.DELETE("/api/sessions/{id}/side", { params: { path: { id: parentId } } });
      if (!response.ok) {
        current.side = closing;
        current.error = errorMessage(error) ?? `The side conversation couldn't be closed (HTTP ${response.status}).`;
      }
    } catch (caught) {
      current.side = closing;
      current.error = caught instanceof Error ? caught.message : "The side conversation couldn't be closed.";
    }
  }

  /** Keeps the side conversation as a session of its own. Returns it, or null when the server refused. */
  async function keep(): Promise<SideConversation | null> {
    const parentId = id.value;
    const current = stateOf(parentId);
    if (!current.side) return null;

    current.error = undefined;
    try {
      const { data, error, response } = await api.POST("/api/sessions/{id}/side/keep", { params: { path: { id: parentId } } });
      if (!response.ok || !data) {
        current.error = errorMessage(error) ?? `The side conversation couldn't be kept (HTTP ${response.status}).`;
        return null;
      }

      current.side = null;
      return data;
    } catch (caught) {
      current.error = caught instanceof Error ? caught.message : "The side conversation couldn't be kept.";
      return null;
    }
  }

  function clearError(): void {
    state.value.error = undefined;
  }

  return {
    side,
    isOpen,
    starting: computed(() => state.value.starting),
    asking: computed(() => state.value.asking),
    error: computed(() => state.value.error),
    ask,
    close,
    keep,
    clearError,
  };
}
