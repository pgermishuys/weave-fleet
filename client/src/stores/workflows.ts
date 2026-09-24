import { defineStore } from "pinia";
import { computed, shallowRef } from "vue";
import { onGlobalEvent } from "@/composables/use-signalr-socket";
import { apiFetch } from "@/lib/api-client";
import { extractApiError } from "@/lib/api-error";
import type { DomainEvent } from "@/lib/domain-events";
import type { DraftedWorkflow, WorkflowCheck, WorkflowDraft, WorkflowFile } from "@/lib/workflow-draft";
import {
  isWorkflowRun,
  WORKFLOW_RUN_EVENT,
  type WorkflowLibrary,
  type WorkflowModelChoice,
  type WorkflowRole,
  type WorkflowRun,
} from "@/lib/workflows";

export interface StartWorkflowRunRequest {
  workflowId: string;
  directory: string;
  request: string;
  baseBranch?: string | null;
  harnessType?: string | null;
  harnessProfileId?: string | null;
  optionalSteps?: string[];
  roleOverrides?: Partial<Record<WorkflowRole, WorkflowModelChoice>>;
  /** "Check with me after each step": every agent step is one the user finishes. */
  checkWithMe?: boolean;
}

/** A save refused because the file changed on disk since it was opened. */
export class WorkflowFileChangedError extends Error {
  constructor(message: string) {
    super(message);
    this.name = "WorkflowFileChangedError";
  }
}

export interface SaveWorkflowFileRequest {
  directory: string;
  workflowId: string;
  /** The hash the file had when it was opened or last saved. */
  hash: string | null;
  /** The File view's text, saved as it is. */
  text?: string;
  /** The designer's draft, written in Fleet's layout. */
  draft?: WorkflowDraft;
  /** Keep mine: save over a file that changed on disk. */
  force?: boolean;
}

async function errorFrom(response: Response, fallback: string): Promise<string> {
  try {
    const body = (await response.json()) as { error?: unknown };
    return extractApiError(body.error ?? body, fallback);
  } catch {
    return fallback;
  }
}

/**
 * Workflow runs, kept current by the `workflow_run` events on the sessions topic. The Sessions list groups a run's
 * step sessions under it, a step session's header shows the stepper, and the dashboard's Needs you counts the runs
 * waiting on you.
 */
export const useWorkflowsStore = defineStore("workflows", () => {
  const runs = shallowRef<Record<string, WorkflowRun>>({});
  const isLoaded = shallowRef(false);
  let loading: Promise<void> | null = null;
  let stopListening: (() => void) | null = null;

  const orderedRuns = computed(() =>
    Object.values(runs.value).sort((a, b) => b.createdAt.localeCompare(a.createdAt)));

  /** Session id → the run it's a step of. */
  const runBySession = computed(() => {
    const index = new Map<string, WorkflowRun>();
    for (const run of Object.values(runs.value)) {
      for (const session of run.sessions) index.set(session.sessionId, run);
    }
    return index;
  });

  /** The sessions whose conversation holds a card waiting on the user. */
  const waitingSessionIds = computed(() => new Set(
    Object.values(runs.value)
      .filter((run) => run.status === "waiting" && run.waiting?.sessionId)
      .map((run) => run.waiting!.sessionId!),
  ));

  function upsert(run: WorkflowRun): void {
    const existing = runs.value[run.id];
    // Events can arrive out of order with a load; keep the newer.
    if (existing && existing.updatedAt > run.updatedAt) return;
    runs.value = { ...runs.value, [run.id]: run };
  }

  function runForSession(sessionId: string | null | undefined): WorkflowRun | null {
    return sessionId ? runBySession.value.get(sessionId) ?? null : null;
  }

  function listen(): void {
    stopListening ??= onGlobalEvent("sessions", (event: DomainEvent) => {
      if ((event.type as string) !== WORKFLOW_RUN_EVENT) return;
      if (isWorkflowRun(event.payload)) upsert(event.payload);
    });
  }

  /** Loads the recent runs once and follows their changes. Call when workflows are on. */
  function ensureLoaded(): Promise<void> {
    listen();
    loading ??= (async () => {
      try {
        const response = await apiFetch("/api/workflows/runs?limit=100");
        if (!response.ok) return;
        const body: unknown = await response.json();
        if (Array.isArray(body)) {
          for (const run of body) {
            if (isWorkflowRun(run)) upsert(run);
          }
        }
        isLoaded.value = true;
      } catch {
        // The next event brings what's needed; the list fills in as runs change.
      } finally {
        loading = null;
      }
    })();
    return loading;
  }

  async function loadLibrary(directory: string | null): Promise<WorkflowLibrary> {
    const query = directory ? `?directory=${encodeURIComponent(directory)}` : "";
    const response = await apiFetch(`/api/workflows${query}`);
    if (!response.ok) throw new Error(await errorFrom(response, "Couldn't load the workflows."));
    return (await response.json()) as WorkflowLibrary;
  }

  async function post(path: string, body: unknown, fallback: string, method = "POST"): Promise<WorkflowRun> {
    const response = await apiFetch(path, {
      method,
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(body),
    });
    if (!response.ok) throw new Error(await errorFrom(response, fallback));
    const run: unknown = await response.json();
    if (!isWorkflowRun(run)) throw new Error(fallback);
    upsert(run);
    return run;
  }

  function start(request: StartWorkflowRunRequest): Promise<WorkflowRun> {
    return post("/api/workflows/runs", request, "Couldn't start the run.");
  }

  /** `checkWithMe` goes with a You step's choice: how involved the user wants to be from here. */
  function answer(runId: string, choice: string, note?: string | null, checkWithMe?: boolean): Promise<WorkflowRun> {
    const body = checkWithMe === undefined ? { choice, note: note ?? null } : { choice, note: note ?? null, checkWithMe };
    return post(`/api/workflows/runs/${encodeURIComponent(runId)}/answer`, body, "Couldn't answer the run.");
  }

  /** Moves on from a step the user finishes: Fleet asks its agent to wrap up, then starts the next step. */
  function moveOn(runId: string, outcome: string | null, note: string | null): Promise<WorkflowRun> {
    return post(`/api/workflows/runs/${encodeURIComponent(runId)}/move-on`, { outcome, note }, "Couldn't move the run on.");
  }

  /** "Check with me after each step", from the run's header. It applies from the next step. */
  function setCheckWithMe(runId: string, on: boolean): Promise<WorkflowRun> {
    return post(`/api/workflows/runs/${encodeURIComponent(runId)}/check-with-me`, { on }, "Couldn't change Check with me.", "PUT");
  }

  function end(runId: string): Promise<WorkflowRun> {
    return post(`/api/workflows/runs/${encodeURIComponent(runId)}/end`, {}, "Couldn't end the run.");
  }

  async function send<T>(path: string, method: string, body: unknown, fallback: string, signal?: AbortSignal): Promise<T> {
    const response = await apiFetch(path, {
      method,
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(body),
      signal,
    });
    if (!response.ok) {
      const message = await errorFrom(response, fallback);
      throw response.status === 409 && method === "PUT" ? new WorkflowFileChangedError(message) : new Error(message);
    }
    return (await response.json()) as T;
  }

  /** The parser's view of the File view's text or the designer's draft: errors with lines, and what Save writes. */
  function check(body: { text: string } | { draft: WorkflowDraft }, signal?: AbortSignal): Promise<WorkflowCheck> {
    return send("/api/workflows/check", "POST", body, "Couldn't check the workflow.", signal);
  }

  function openFile(directory: string, workflowId: string): Promise<WorkflowFile> {
    return send("/api/workflows/files/open", "POST", { directory, workflowId }, "Couldn't open the workflow file.");
  }

  /** Saves over the file; throws {@link WorkflowFileChangedError} when it changed on disk since. */
  function saveFile(request: SaveWorkflowFileRequest): Promise<WorkflowFile> {
    return send("/api/workflows/files", "PUT", request, "Couldn't save the workflow file.");
  }

  /** New workflow, or Duplicate when `workflowId` names a built-in: a new file in the repo's `.weave/workflows`. */
  function createFile(directory: string, name: string, workflowId?: string): Promise<WorkflowFile> {
    return send("/api/workflows/files", "POST", { directory, name, workflowId: workflowId ?? null }, "Couldn't create the workflow file.");
  }

  /**
   * Saves a drafted workflow for the first time: a new file named after the workflow, with the File view's text as it
   * is or the designer's draft. A name that's taken is refused, as New's is.
   */
  function createDrafted(directory: string, content: { text: string } | { draft: WorkflowDraft }): Promise<WorkflowFile> {
    return send("/api/workflows/files", "POST", { directory, ...content }, "Couldn't save the workflow file.");
  }

  /** Save as workflow…: the model describes the session's process as a workflow, off the record. Nothing is written. */
  function draftFromSession(sessionId: string, signal?: AbortSignal): Promise<DraftedWorkflow> {
    return send("/api/workflows/drafts/from-session", "POST", { sessionId }, "Couldn't draft a workflow from this session.", signal);
  }

  /** New workflow → Describe it: one question with no session, on the Standard role's model. Nothing is written. */
  function draftFromDescription(
    request: { directory: string; description: string; harnessType: string | null },
    signal?: AbortSignal,
  ): Promise<DraftedWorkflow> {
    return send("/api/workflows/drafts/from-description", "POST", request, "Couldn't draft the workflow.", signal);
  }

  return {
    runs,
    orderedRuns,
    isLoaded,
    waitingSessionIds,
    runForSession,
    upsert,
    ensureLoaded,
    loadLibrary,
    start,
    answer,
    moveOn,
    setCheckWithMe,
    end,
    check,
    openFile,
    saveFile,
    createFile,
    createDrafted,
    draftFromSession,
    draftFromDescription,
  };
});
