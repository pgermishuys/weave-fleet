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

  async function runAutomation(id: string): Promise<void> {
    await send(itemPath(id, "/run"), { method: "POST" });
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
    fetchEventCatalog,
  };
});
