/**
 * Provide/inject contract for the session detail panel.
 *
 * This file is intentionally version-agnostic — it defines the minimum interface
 * that both V1 and V2 session action composables satisfy, so that
 * `SessionDetailPanel` can be driven by either without importing from either.
 */
import { type ComputedRef, type InjectionKey, type ShallowRef, inject, provide } from "vue";
import type { ResumeSessionResponse, SessionListItem } from "@/lib/api-types";

// ---------------------------------------------------------------------------
// Action composable interfaces (duck-typed — matched by both V1 and V2)
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

export interface SessionUnarchiveActions {
  unarchiveSession: (sessionId: string) => Promise<void>;
  isUnarchiving: Readonly<ShallowRef<boolean>>;
  error: Readonly<ShallowRef<string | undefined>>;
}

// ---------------------------------------------------------------------------
// Context shape
// ---------------------------------------------------------------------------

export interface SessionDetailContext {
  /**
   * Base path for session API calls, e.g. "/api/sessions" or "/api/sessions-v1".
   */
  apiBasePath: string;

  /**
   * Route id used when navigating after a resume, e.g. "/sessions/$id" or
   * "/sessions-v1/$id".
   */
  sessionRoutePath: string;

  /**
   * Whether the Fork action is available. V1 has no fork endpoint.
   */
  supportsFork: boolean;

  /**
   * Whether the Archive/Unarchive actions are available. V1 has no archive capability.
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
  unarchive: SessionUnarchiveActions;
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
      + "Make sure a parent renders SessionsV2RightPanel or SessionsV1RightPanel.",
    );
  }

  return ctx;
}
