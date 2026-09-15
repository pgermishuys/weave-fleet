import { defineStore } from "pinia";
import { shallowRef } from "vue";
import { apiFetch } from "@/lib/api-client";

export interface Automation {
  id: string;
  name: string;
  prompt: string;
  triggerType: string;
  triggerConfig: string;
  maxConcurrentRuns: number;
  maxRunsPerHour: number;
  timeoutMinutes: number;
  isEnabled: boolean;
  workspaceId: string | null;
  model: string | null;
  agent: string | null;
  createdAt: string;
  updatedAt: string | null;
  targetType?: string;
  targetTags?: readonly string[];
  /** The IANA zone the schedule's cron is read in; null means UTC (automations made before zones were kept). */
  timeZone?: string | null;
  /** "worktree" (a new worktree of the folder each run) or "existing"; null for automations made before it was kept. */
  isolation?: string | null;
  /** Where a run's worktree starts; null for the repository's default. */
  baseBranch?: string | null;
  /** When it runs next (ISO, UTC); null when it's off or waits for an event. */
  nextRunAt?: string | null;
  lastRun?: AutomationRun | null;
}

export type AutomationRunState = "starting" | "running" | "done" | "failed" | "skipped";

export interface AutomationRun {
  id: string;
  automationId: string;
  /** "schedule", "catch_up", "once", "manual", or the event type. */
  trigger: string;
  scheduledFor: string | null;
  startedAt: string;
  state: AutomationRunState;
  sessionId: string | null;
  instanceId: string | null;
  /** Why it failed or was skipped. */
  error: string | null;
}

/** A new automation's starting point from a session ("Repeat on a schedule…"). */
export interface AutomationDraft {
  prompt: string;
  /** The folder it ran in; null for a quick chat. */
  folder: string | null;
  isolation: "worktree" | "existing";
}

export interface CreateAutomationRequest {
  name: string;
  prompt: string;
  triggerType: string;
  triggerConfig: string;
  maxConcurrentRuns?: number;
  maxRunsPerHour?: number;
  timeoutMinutes?: number;
  workspaceId?: string | null;
  model?: string | null;
  agent?: string | null;
  targetType?: string;
  targetTags?: string[];
  timeZone?: string | null;
  isolation?: string | null;
  baseBranch?: string | null;
}

export type UpdateAutomationRequest = CreateAutomationRequest;

const base = "/api/automations";
const itemPath = (id: string, suffix = "") => `${base}/${encodeURIComponent(id)}${suffix}`;

async function readError(response: Response): Promise<Error> {
  const data = (await response.json().catch(() => ({}))) as { error?: string };
  return new Error(data.error ?? `HTTP ${response.status}`);
}

async function send(url: string, init: RequestInit): Promise<Response> {
  const response = await apiFetch(url, init);
  if (!response.ok) {
    throw await readError(response);
  }
  return response;
}

function jsonInit(method: string, body: unknown): RequestInit {
  return { method, headers: { "Content-Type": "application/json" }, body: JSON.stringify(body) };
}

/**
 * The automations list, shared by the sidebar and the automation page so a change made on one shows on the
 * other. Every change reloads the list from the server.
 */
export const useAutomationsStore = defineStore("automations", () => {
  const automations = shallowRef<Automation[]>([]);
  /** True until the first load finishes; later reloads keep showing the list they're replacing. */
  const isLoading = shallowRef(true);
  const error = shallowRef<string | undefined>(undefined);
  let pending: Promise<void> | null = null;

  async function fetchAll(): Promise<void> {
    error.value = undefined;
    try {
      const response = await send(base, {});
      const result = (await response.json()) as { automations: Automation[] };
      automations.value = result.automations;
    } catch (fetchError) {
      error.value = fetchError instanceof Error ? fetchError.message : "Failed to load automations";
    } finally {
      isLoading.value = false;
    }
  }

  /** Reloads the list; calls made while a load is in flight share it. */
  function refresh(): Promise<void> {
    pending ??= fetchAll().finally(() => {
      pending = null;
    });
    return pending;
  }

  async function createAutomation(request: CreateAutomationRequest): Promise<Automation> {
    const response = await send(base, jsonInit("POST", request));
    const automation = (await response.json()) as Automation;
    await refresh();
    return automation;
  }

  async function updateAutomation(id: string, request: UpdateAutomationRequest): Promise<void> {
    await send(itemPath(id), jsonInit("PUT", request));
    await refresh();
  }

  async function deleteAutomation(id: string): Promise<void> {
    await send(itemPath(id), { method: "DELETE" });
    await refresh();
  }

  async function enableAutomation(id: string): Promise<void> {
    await send(itemPath(id, "/enable"), { method: "POST" });
    await refresh();
  }

  async function disableAutomation(id: string): Promise<void> {
    await send(itemPath(id, "/disable"), { method: "POST" });
    await refresh();
  }

  /** Starts a run now; the answer is the run, still starting, and the list reloads to show it. */
  async function runAutomation(id: string): Promise<AutomationRun> {
    const response = await send(itemPath(id, "/run"), { method: "POST" });
    const run = (await response.json()) as AutomationRun;
    await refresh();
    return run;
  }

  /** An automation's runs, newest first. */
  async function fetchRuns(id: string): Promise<AutomationRun[]> {
    const response = await send(itemPath(id, "/runs"), {});
    return ((await response.json()) as { runs: AutomationRun[] }).runs;
  }

  async function fetchDraftFromSession(sessionId: string): Promise<AutomationDraft> {
    const response = await send(`${base}/draft-from-session/${encodeURIComponent(sessionId)}`, {});
    return (await response.json()) as AutomationDraft;
  }

  async function fetchEventCatalog(): Promise<string[]> {
    const response = await send(`${base}/event-catalog`, {});
    return (await response.json()) as string[];
  }

  return {
    automations,
    isLoading,
    error,
    refresh,
    createAutomation,
    updateAutomation,
    deleteAutomation,
    enableAutomation,
    disableAutomation,
    runAutomation,
    fetchRuns,
    fetchDraftFromSession,
    fetchEventCatalog,
  };
});
