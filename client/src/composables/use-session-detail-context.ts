/**
 * Provide/inject contract for the session detail panel.
 */
import { type ComputedRef, type InjectionKey, type ShallowRef, inject, provide } from "vue";
import type { ResumeSessionResponse, SessionListItem } from "@/lib/api-types";

// ---------------------------------------------------------------------------
// Action composable interfaces
// ---------------------------------------------------------------------------

export interface SessionAbortActions {
  abortSession: (sessionId: string, instanceId: string) => Promise<void>;
  isAborting: Readonly<ShallowRef<boolean>>;
  error: Readonly<ShallowRef<string | undefined>>;
}

export interface SessionArchiveActions {
  archiveSession: (sessionId: string) => Promise<void>;
  isArchiving: Readonly<ShallowRef<boolean>>;
  error: Readonly<ShallowRef<string | undefined>>;
}

export interface SessionDeleteActions {
  deleteSession: (sessionId: string, instanceId: string) => Promise<void>;
  isDeleting: Readonly<ShallowRef<boolean>>;
  error: Readonly<ShallowRef<string | undefined>>;
}

export interface SessionRenameActions {
  renameSession: (sessionId: string, title: string, onSuccess?: () => void) => Promise<void>;
  isLoading: Readonly<ShallowRef<boolean>>;
  error: Readonly<ShallowRef<string | undefined>>;
}

export interface SessionResumeActions {
  resumeSession: (sessionId: string) => Promise<ResumeSessionResponse>;
  isResuming: ComputedRef<boolean>;
  resumingSessionId: Readonly<ShallowRef<string | null>>;
  error: Readonly<ShallowRef<string | undefined>>;
}

export interface SessionTerminateActions {
  terminateSession: (sessionId: string, instanceId: string) => Promise<void>;
  isTerminating: Readonly<ShallowRef<boolean>>;
  error: Readonly<ShallowRef<string | undefined>>;
}

// ---------------------------------------------------------------------------
// Context shape
// ---------------------------------------------------------------------------

export interface SessionDetailContext {
  /**
   * Base path for session API calls, e.g. "/api/sessions".
   */
  apiBasePath: string;

  /**
   * Route id used when navigating after a resume, e.g. "/sessions/$id".
   */
  sessionRoutePath: string;

  /**
   * Whether the Fork action is available.
   */
  supportsFork: boolean;

  /**
   * Whether the Archive action is available.
   */
  supportsArchive: boolean;

  /**
   * How actions are rendered — "card" shows a labelled card section,
   * "toolbar" shows a compact icon-only strip at the top of the panel.
   */
  actionsLayout: "card" | "toolbar";

  /**
   * Propagate optimistic session state patches to the active store.
   */
  patchSession: (sessionId: string, patch: Partial<SessionListItem>) => void;

  abort: SessionAbortActions;
  archive: SessionArchiveActions;
  delete: SessionDeleteActions;
  rename: SessionRenameActions;
  resume: SessionResumeActions;
  terminate: SessionTerminateActions;
}

// ---------------------------------------------------------------------------
// Injection key + helpers
// ---------------------------------------------------------------------------

export const SessionDetailContextKey: InjectionKey<SessionDetailContext> = Symbol("SessionDetailContext");

export function provideSessionDetailContext(ctx: SessionDetailContext): void {
  provide(SessionDetailContextKey, ctx);
}

export function useSessionDetailContext(): SessionDetailContext {
  const ctx = inject(SessionDetailContextKey);
  if (!ctx) {
    throw new Error(
      "useSessionDetailContext() was called outside a component that provides SessionDetailContext. "
      + "Make sure a parent renders SessionsV2RightPanel.",
    );
  }

  return ctx;
}
