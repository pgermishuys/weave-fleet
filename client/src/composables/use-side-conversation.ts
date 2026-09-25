import { computed, reactive, toValue, watch, type MaybeRefOrGetter } from "vue";
import { api } from "@/api/client";
import type { components } from "@/api/generated/schema";
import { useDraftState } from "@/composables/use-draft-state";
import { showSentPrompt } from "@/composables/use-send-prompt";
import { readStoredDraft, sideDraftKey, sideMainDraftKey, storedDraftKeys, writeStoredDraft } from "@/lib/draft-storage";

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

/** Which composer draft: the session's, or its side conversation's. */
export type DraftTarget = "main" | "side";

/** What a side conversation's stream reports: whether it's working, and its newest answer. */
export interface SideProgress {
  working: boolean;
  latestAnswer: string | null;
  latestAnswerId: string | null;
}

interface SideConversationState {
  side: SideConversation | null;
  loaded: boolean;
  /** A question is on its way; before the first one's answer, the side conversation is being forked. */
  asking: boolean;
  /** The question that started the side conversation, shown while the fork is made. */
  starting: string | null;
  error?: string;
  /** Its conversation has reported since the page loaded (it stays mounted while it's folded). */
  reported: boolean;
  working: boolean;
  latestAnswer: string | null;
  latestAnswerId: string | null;
  /** An answer that landed on this page while it was folded: its dot pulses. One found on a reload doesn't. */
  pulseAnswerId: string | null;
  /** The draft that isn't in the composer: the side's while it's folded, the session's while it's open. */
  drafts: Record<DraftTarget, string>;
  /** A side conversation discarded moments ago, which Undo can still bring back. */
  discarded: SideConversation | null;
  /** How long Undo was offered for when it was shown: 8 s, or what the server says is left after a reload. */
  undoMs: number;
  discardTimer: ReturnType<typeof setTimeout> | null;
}

/**
 * Per session, shared by the panel and the composer, and kept while the user is on other sessions. The server has
 * the side conversation, whether it's minimized, the newest answer the user has seen, and a discard's Undo window
 * (all survive a reload); the browser keeps the drafts.
 */
const states = reactive<Record<string, SideConversationState>>({});

function stateOf(sessionId: string): SideConversationState {
  states[sessionId] ??= {
    side: null,
    loaded: false,
    asking: false,
    starting: null,
    reported: false,
    working: false,
    latestAnswer: null,
    latestAnswerId: null,
    pulseAnswerId: null,
    drafts: { main: "", side: "" },
    discarded: null,
    undoMs: SIDE_DISCARD_UNDO_MS,
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

/** The storage key of the draft for `target` that isn't in the composer, or null when there's no side conversation. */
function stashKey(sessionId: string, state: SideConversationState, target: DraftTarget): string | null {
  if (target === "main") return sideMainDraftKey(sessionId);
  const sideId = state.side?.sessionId ?? state.discarded?.sessionId;
  return sideId ? sideDraftKey(sessionId, sideId) : null;
}

/** Forgets the draft of a side conversation that's gone for good. */
function dropSideDraft(sessionId: string, sideId: string): void {
  writeStoredDraft(sideDraftKey(sessionId, sideId), "");
}

/** Shows Undo for a discarded side conversation for `ms`, then lets it go (the server deletes it). */
function offerUndo(sessionId: string, state: SideConversationState, discarded: SideConversation, ms: number): void {
  if (state.discardTimer) clearTimeout(state.discardTimer);
  state.discarded = discarded;
  state.undoMs = ms;
  state.discardTimer = setTimeout(() => {
    state.discarded = null;
    state.discardTimer = null;
    state.drafts.side = "";
    dropSideDraft(sessionId, discarded.sessionId);
  }, ms);
}

async function load(sessionId: string): Promise<void> {
  const state = stateOf(sessionId);
  try {
    const { data, response } = await api.GET("/api/sessions/{id}/side", { params: { path: { id: sessionId } } });
    // A question asked while this was loading already knows better.
    if (!state.asking) {
      state.side = response.status === 200 && data ? data : null;
    }

    // A discard still in its Undo window (the page was reloaded, or left and come back to): Undo again, for what's left.
    if (!state.side && !state.discarded) {
      const discarded = await api.GET("/api/sessions/{id}/side/discarded", { params: { path: { id: sessionId } } });
      if (discarded.response.status === 200 && discarded.data && discarded.data.undoRemainingMs > 0 && !state.side) {
        offerUndo(sessionId, state, discarded.data.sideConversation, discarded.data.undoRemainingMs);
      }
    }
  } catch {
    // The panel stays closed; the next visit asks again.
    return;
  }

  state.loaded = true;
  restoreDrafts(sessionId, state);
}

/** Brings back the stashed drafts after a reload, and forgets those of side conversations that are gone. */
function restoreDrafts(sessionId: string, state: SideConversationState): void {
  const sideId = state.side?.sessionId ?? state.discarded?.sessionId;
  for (const key of storedDraftKeys(`side.${sessionId}.`)) {
    if (!sideId || key !== sideDraftKey(sessionId, sideId)) writeStoredDraft(key, "");
  }

  if (sideId) {
    state.drafts.side = readStoredDraft(sideDraftKey(sessionId, sideId)) ?? state.drafts.side;
    state.drafts.main = readStoredDraft(sideMainDraftKey(sessionId)) ?? state.drafts.main;
    return;
  }

  // The side conversation went for good while the page was away: the session's draft goes back in the composer.
  const main = readStoredDraft(sideMainDraftKey(sessionId));
  if (main !== null) {
    useDraftState(sessionId, { agentId: "", modelId: "" }).setText(main);
    writeStoredDraft(sideMainDraftKey(sessionId), "");
  }
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
  /** It's folded and its newest finished answer is newer than the newest one the user has seen with it open. */
  const unread = computed(() => {
    const current = state.value;
    return minimized.value
      && current.reported
      && !current.working
      && current.latestAnswerId !== null
      && current.latestAnswerId !== (current.side?.seenAnswerId ?? null);
  });
  /** The unread dot pulses for an answer that landed on this page, not for one found after a reload. */
  const pulse = computed(() => unread.value && state.value.pulseAnswerId === state.value.latestAnswerId);

  /** Records `answerId` as seen, on the server (so a reload knows) and here. */
  async function markSeen(parentId: string, answerId: string): Promise<void> {
    const current = stateOf(parentId);
    if (!current.side || current.side.seenAnswerId === answerId) return;
    current.side = { ...current.side, seenAnswerId: answerId };
    try {
      await api.PUT("/api/sessions/{id}/side/seen", { params: { path: { id: parentId } }, body: { answerId } });
    } catch {
      // Seen here; a reload may call it new again.
    }
  }

  /**
   * Asks `question` in the side conversation, starting one when there's none. The question shows in the panel at once;
   * a question the server refuses sets `error` and returns false. A new one ends a discarded one's Undo.
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

      // The server deleted a discarded one to start this: its Undo goes.
      if (current.discarded && current.discarded.sessionId !== data.sideConversation.sessionId) {
        if (current.discardTimer) clearTimeout(current.discardTimer);
        dropSideDraft(parentId, current.discarded.sessionId);
        current.discarded = null;
        current.discardTimer = null;
        current.drafts.side = "";
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
    if (!value) {
      current.pulseAnswerId = null;
      if (current.latestAnswerId && !current.working) void markSeen(parentId, current.latestAnswerId);
    }
    try {
      const { data, error, response } = await api.PUT("/api/sessions/{id}/side/minimized", {
        params: { path: { id: parentId } },
        body: { minimized: value },
      });
      if (!response.ok || !data) {
        current.error = errorMessage(error) ?? `The side conversation couldn't be ${value ? "minimized" : "opened"} (HTTP ${response.status}).`;
        return;
      }
      if (current.side?.sessionId === data.sessionId) current.side = { ...data, seenAnswerId: current.side.seenAnswerId };
    } catch (caught) {
      current.error = caught instanceof Error ? caught.message : "The side conversation couldn't be changed.";
    }
  }

  function toggleMinimized(): Promise<void> {
    return setMinimized(!minimized.value);
  }

  /** Its conversation's report. An answer that finishes while it's open is seen; one while it's folded is news. */
  function reportProgress(progress: SideProgress): void {
    const parentId = id.value;
    const current = state.value;
    const first = !current.reported;
    const wasWorking = current.working;
    const previousId = current.latestAnswerId;
    current.reported = true;
    current.working = progress.working;
    current.latestAnswer = progress.latestAnswer;
    current.latestAnswerId = progress.latestAnswerId;

    if (progress.working || !progress.latestAnswerId) return;
    if (!minimized.value) {
      void markSeen(parentId, progress.latestAnswerId);
    } else if (!first && (wasWorking || progress.latestAnswerId !== previousId)) {
      current.pulseAnswerId = progress.latestAnswerId;
    }
  }

  /** Puts away the draft for `target` that's leaving the composer, in memory and in the browser. */
  function putAwayDraft(target: DraftTarget, text: string): void {
    const current = state.value;
    current.drafts[target] = text;
    const key = stashKey(id.value, current, target);
    if (key) writeStoredDraft(key, text);
  }

  /** Takes out the put-away draft for `target`, which goes back in the composer. */
  function takeOutDraft(target: DraftTarget): string {
    const current = state.value;
    const text = current.drafts[target];
    current.drafts[target] = "";
    const key = stashKey(id.value, current, target);
    if (key) writeStoredDraft(key, "");
    return text;
  }

  /**
   * Discards the side conversation: it goes at once, and the server deletes the fork once the undo window has passed.
   * Undo, offered for {@link SIDE_DISCARD_UNDO_MS}, brings it back as it was.
   */
  async function close(): Promise<void> {
    const parentId = id.value;
    const current = stateOf(parentId);
    const closing = current.side;
    if (!closing) return;
    current.error = undefined;
    offerUndo(parentId, current, closing, SIDE_DISCARD_UNDO_MS);
    current.side = null;

    try {
      const { error, response } = await api.DELETE("/api/sessions/{id}/side", { params: { path: { id: parentId } } });
      if (!response.ok) {
        restoreClosed(current, closing);
        current.error = errorMessage(error) ?? `The side conversation couldn't be discarded (HTTP ${response.status}).`;
      }
    } catch (caught) {
      restoreClosed(current, closing);
      current.error = caught instanceof Error ? caught.message : "The side conversation couldn't be discarded.";
    }
  }

  function restoreClosed(current: SideConversationState, closing: SideConversation): void {
    if (current.discardTimer) clearTimeout(current.discardTimer);
    current.discardTimer = null;
    current.discarded = null;
    current.side = closing;
  }

  /** Brings back the side conversation discarded moments ago, as it was (minimized or open, draft and all). */
  async function undoDiscard(): Promise<boolean> {
    const parentId = id.value;
    const current = stateOf(parentId);
    const discarded = current.discarded;
    if (!discarded) return false;

    if (current.discardTimer) clearTimeout(current.discardTimer);
    current.discardTimer = null;
    try {
      const { data, error, response } = await api.POST("/api/sessions/{id}/side/restore", { params: { path: { id: parentId } } });
      if (!response.ok || !data) {
        current.discarded = null;
        current.error = errorMessage(error) ?? `The side conversation couldn't be brought back (HTTP ${response.status}).`;
        return false;
      }
      current.side = { ...discarded, ...data };
      current.discarded = null;
      return true;
    } catch (caught) {
      current.discarded = null;
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
    pulse,
    working: computed(() => state.value.working),
    latestAnswer: computed(() => state.value.latestAnswer),
    discarded: computed(() => state.value.discarded),
    undoMs: computed(() => state.value.undoMs),
    starting: computed(() => state.value.starting),
    asking: computed(() => state.value.asking),
    error: computed(() => state.value.error),
    ask,
    setMinimized,
    toggleMinimized,
    reportProgress,
    putAwayDraft,
    takeOutDraft,
    close,
    undoDiscard,
    keep,
    clearError,
  };
}
