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

/** How long Undo is offered after a discard. The server keeps the fork a little longer, so a late Undo still finds it. */
export const SIDE_DISCARD_UNDO_MS = 8_000;

interface SideConversationState {
  side: SideConversation | null;
  loaded: boolean;
  /** A question is on its way; before the first one's answer, the side conversation is being forked. */
  asking: boolean;
  /** The question that started the side conversation, shown while the fork is made. */
  starting: string | null;
  error?: string;
  /** A turn is running in it (reported by its conversation, which stays mounted while it's folded). */
  working: boolean;
  /** The newest answer's text, for the tab's peek. */
  latestAnswer: string | null;
  /** The newest answer the user has had open in front of them; undefined until the conversation first reports. */
  seenAnswer: string | null | undefined;
  /** What was typed for the side conversation while it's folded, and for the session while it's open. */
  drafts: { side: string; main: string };
  /** A side conversation discarded moments ago, which Undo can still bring back. */
  discarded: SideConversation | null;
  discardTimer: ReturnType<typeof setTimeout> | null;
}

/**
 * Per session, shared by the panel and the composer, and kept while the user is on other sessions. The server has
 * the side conversation and whether it's minimized (both survive a reload); this mirrors them and keeps what's only
 * the page's: drafts, the unread answer, Undo.
 */
const states = reactive<Record<string, SideConversationState>>({});

function stateOf(sessionId: string): SideConversationState {
  states[sessionId] ??= {
    side: null,
    loaded: false,
    asking: false,
    starting: null,
    working: false,
    latestAnswer: null,
    seenAnswer: undefined,
    drafts: { side: "", main: "" },
    discarded: null,
    discardTimer: null,
  };
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
  for (const key of Object.keys(states)) {
    const timer = states[key].discardTimer;
    if (timer) clearTimeout(timer);
    delete states[key];
  }
}

export function useSideConversation(sessionId: MaybeRefOrGetter<string>) {
  const id = computed(() => toValue(sessionId));
  const state = computed(() => stateOf(id.value));

  watch(id, (next) => {
    if (next && !stateOf(next).loaded) void load(next);
  }, { immediate: true });

  const side = computed(() => state.value.side);
  const minimized = computed(() => state.value.side?.minimized === true);
  /** The panel is open: a side conversation that isn't minimized, or the first question making one. */
  const isOpen = computed(() => (state.value.side !== null && !minimized.value) || state.value.starting !== null);
  /** An answer landed while it was folded, and the user hasn't opened it since. */
  const unread = computed(() => {
    const current = state.value;
    return minimized.value
      && !current.working
      && current.latestAnswer !== null
      && current.seenAnswer !== undefined
      && current.latestAnswer !== current.seenAnswer;
  });

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

      // Asking a minimized one opens it (the server says so too).
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

  /** Folds the side conversation into its tab, or opens it again. Kept on the server; shown at once. */
  async function setMinimized(value: boolean): Promise<void> {
    const parentId = id.value;
    const current = stateOf(parentId);
    if (!current.side || current.side.minimized === value) return;

    current.side = { ...current.side, minimized: value };
    if (!value) current.seenAnswer = current.latestAnswer;
    try {
      const { data, error, response } = await api.PUT("/api/sessions/{id}/side/minimized", {
        params: { path: { id: parentId } },
        body: { minimized: value },
      });
      if (!response.ok || !data) {
        current.error = errorMessage(error) ?? `The side conversation couldn't be ${value ? "minimized" : "opened"} (HTTP ${response.status}).`;
        return;
      }
      if (current.side?.sessionId === data.sessionId) current.side = data;
    } catch (caught) {
      current.error = caught instanceof Error ? caught.message : "The side conversation couldn't be changed.";
    }
  }

  function toggleMinimized(): Promise<void> {
    return setMinimized(!minimized.value);
  }

  /** Its conversation's report: whether it's working, and its newest answer. Seen while it's open. */
  function reportProgress(progress: { working: boolean; latestAnswer: string | null }): void {
    const current = state.value;
    current.working = progress.working;
    current.latestAnswer = progress.latestAnswer;
    if (current.seenAnswer === undefined || !minimized.value) current.seenAnswer = progress.latestAnswer;
  }

  /**
   * Discards the side conversation: it goes at once, and the server deletes the fork once the undo window has passed.
   * Undo, offered for {@link SIDE_DISCARD_UNDO_MS}, brings it back as it was.
   */
  async function close(): Promise<void> {
    const parentId = id.value;
    const current = stateOf(parentId);
    const closing = current.side;
    current.side = null;
    current.error = undefined;
    if (!closing) return;

    if (current.discardTimer) clearTimeout(current.discardTimer);
    current.discarded = closing;
    current.discardTimer = setTimeout(() => {
      current.discarded = null;
      current.discardTimer = null;
    }, SIDE_DISCARD_UNDO_MS);

    try {
      const { error, response } = await api.DELETE("/api/sessions/{id}/side", { params: { path: { id: parentId } } });
      if (!response.ok) {
        current.side = closing;
        current.discarded = null;
        current.error = errorMessage(error) ?? `The side conversation couldn't be discarded (HTTP ${response.status}).`;
      }
    } catch (caught) {
      current.side = closing;
      current.discarded = null;
      current.error = caught instanceof Error ? caught.message : "The side conversation couldn't be discarded.";
    }
  }

  /** Brings back the side conversation discarded moments ago, as it was (minimized or open, draft and all). */
  async function undoDiscard(): Promise<boolean> {
    const parentId = id.value;
    const current = stateOf(parentId);
    if (!current.discarded) return false;

    if (current.discardTimer) clearTimeout(current.discardTimer);
    current.discardTimer = null;
    const discarded = current.discarded;
    current.discarded = null;
    try {
      const { data, error, response } = await api.POST("/api/sessions/{id}/side/restore", { params: { path: { id: parentId } } });
      if (!response.ok || !data) {
        current.error = errorMessage(error) ?? `The side conversation couldn't be brought back (HTTP ${response.status}).`;
        return false;
      }
      current.side = { ...discarded, ...data };
      return true;
    } catch (caught) {
      current.error = caught instanceof Error ? caught.message : "The side conversation couldn't be brought back.";
      return false;
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
    minimized,
    unread,
    working: computed(() => state.value.working),
    latestAnswer: computed(() => state.value.latestAnswer),
    drafts: computed(() => state.value.drafts),
    discarded: computed(() => state.value.discarded),
    starting: computed(() => state.value.starting),
    asking: computed(() => state.value.asking),
    error: computed(() => state.value.error),
    ask,
    setMinimized,
    toggleMinimized,
    reportProgress,
    close,
    undoDiscard,
    keep,
    clearError,
  };
}
