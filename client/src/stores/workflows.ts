import { defineStore } from "pinia";
import { computed, shallowRef } from "vue";
import { onGlobalEvent } from "@/composables/use-signalr-socket";
import { apiFetch } from "@/lib/api-client";
import { extractApiError } from "@/lib/api-error";
import type { DomainEvent } from "@/lib/domain-events";
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

  async function post(path: string, body: unknown, fallback: string): Promise<WorkflowRun> {
    const response = await apiFetch(path, {
      method: "POST",
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

  function answer(runId: string, choice: string, note?: string | null): Promise<WorkflowRun> {
    return post(`/api/workflows/runs/${encodeURIComponent(runId)}/answer`, { choice, note: note ?? null }, "Couldn't answer the run.");
  }

  function end(runId: string): Promise<WorkflowRun> {
    return post(`/api/workflows/runs/${encodeURIComponent(runId)}/end`, {}, "Couldn't end the run.");
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
    end,
  };
});
