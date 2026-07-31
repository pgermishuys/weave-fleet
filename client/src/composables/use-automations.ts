import { computed, readonly, ref, shallowRef, toValue, type ComputedRef, type MaybeRefOrGetter, type Ref, type ShallowRef } from "vue";
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
}

export type UpdateAutomationRequest = CreateAutomationRequest;

export interface UseAutomationsResult {
  automations: Readonly<Ref<readonly Automation[]>>;
  isLoading: Readonly<ShallowRef<boolean>>;
  error: Readonly<ShallowRef<string | undefined>>;
  workspaceId: ComputedRef<string | null>;
  refresh: () => Promise<void>;
  createAutomation: (request: CreateAutomationRequest) => Promise<Automation>;
  updateAutomation: (id: string, request: UpdateAutomationRequest) => Promise<void>;
  deleteAutomation: (id: string) => Promise<void>;
  enableAutomation: (id: string) => Promise<void>;
  disableAutomation: (id: string) => Promise<void>;
  runAutomation: (id: string) => Promise<void>;
  fetchEventCatalog: () => Promise<string[]>;
}

export function useAutomations(workspaceIdInput?: MaybeRefOrGetter<string | null>): UseAutomationsResult {
  const automations = ref<Automation[]>([]);
  const isLoading = shallowRef(true);
  const error = shallowRef<string | undefined>(undefined);

  const workspaceId = computed(() => toValue(workspaceIdInput) ?? null);

  async function fetchAutomations(): Promise<void> {
    isLoading.value = true;
    error.value = undefined;

    try {
      const params = new URLSearchParams();
      if (workspaceId.value) {
        params.set("workspaceId", workspaceId.value);
      }

      const queryString = params.toString();
      const url = queryString ? `/api/automations?${queryString}` : "/api/automations";

      const response = await apiFetch(url);
      if (!response.ok) {
        const data = (await response.json().catch(() => ({}))) as { error?: string };
        throw new Error(data.error ?? `HTTP ${response.status}`);
      }

      const result = (await response.json()) as { automations: Automation[] };
      automations.value = result.automations;
    } catch (fetchError) {
      error.value = fetchError instanceof Error ? fetchError.message : "Failed to load automations";
    } finally {
      isLoading.value = false;
    }
  }

  async function createAutomation(request: CreateAutomationRequest): Promise<Automation> {
    const response = await apiFetch("/api/automations", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(request),
    });

    if (!response.ok) {
      const data = (await response.json().catch(() => ({}))) as { error?: string };
      throw new Error(data.error ?? `HTTP ${response.status}`);
    }

    const automation = (await response.json()) as Automation;
    await fetchAutomations();
    return automation;
  }

  async function updateAutomation(id: string, request: UpdateAutomationRequest): Promise<void> {
    const response = await apiFetch(`/api/automations/${encodeURIComponent(id)}`, {
      method: "PUT",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(request),
    });

    if (!response.ok) {
      const data = (await response.json().catch(() => ({}))) as { error?: string };
      throw new Error(data.error ?? `HTTP ${response.status}`);
    }

    await fetchAutomations();
  }

  async function deleteAutomation(id: string): Promise<void> {
    const response = await apiFetch(`/api/automations/${encodeURIComponent(id)}`, {
      method: "DELETE",
    });

    if (!response.ok) {
      const data = (await response.json().catch(() => ({}))) as { error?: string };
      throw new Error(data.error ?? `HTTP ${response.status}`);
    }

    await fetchAutomations();
  }

  async function enableAutomation(id: string): Promise<void> {
    const response = await apiFetch(`/api/automations/${encodeURIComponent(id)}/enable`, {
      method: "POST",
    });

    if (!response.ok) {
      const data = (await response.json().catch(() => ({}))) as { error?: string };
      throw new Error(data.error ?? `HTTP ${response.status}`);
    }

    await fetchAutomations();
  }

  async function disableAutomation(id: string): Promise<void> {
    const response = await apiFetch(`/api/automations/${encodeURIComponent(id)}/disable`, {
      method: "POST",
    });

    if (!response.ok) {
      const data = (await response.json().catch(() => ({}))) as { error?: string };
      throw new Error(data.error ?? `HTTP ${response.status}`);
    }

    await fetchAutomations();
  }

  async function runAutomation(id: string): Promise<void> {
    const response = await apiFetch(`/api/automations/${encodeURIComponent(id)}/run`, {
      method: "POST",
    });

    if (!response.ok) {
      const data = (await response.json().catch(() => ({}))) as { error?: string };
      throw new Error(data.error ?? `HTTP ${response.status}`);
    }
  }

  async function fetchEventCatalog(): Promise<string[]> {
    const response = await apiFetch("/api/automations/event-catalog");

    if (!response.ok) {
      const data = (await response.json().catch(() => ({}))) as { error?: string };
      throw new Error(data.error ?? `HTTP ${response.status}`);
    }

    return (await response.json()) as string[];
  }

  void fetchAutomations();

  return {
    automations: readonly(automations),
    isLoading: readonly(isLoading),
    error: readonly(error),
    workspaceId,
    refresh: fetchAutomations,
    createAutomation,
    updateAutomation,
    deleteAutomation,
    enableAutomation,
    disableAutomation,
    runAutomation,
    fetchEventCatalog,
  };
}
