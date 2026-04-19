import {
  readonly,
  ref,
  shallowRef,
  toValue,
  watch,
  type MaybeRefOrGetter,
  type Ref,
  type ShallowRef,
} from "vue";
import type { ProjectResponse } from "@/lib/api-types";
import { apiFetch } from "@/lib/api-client";

export interface UseProjectsOptions {
  enabled?: MaybeRefOrGetter<boolean>;
}

export interface UseProjectsResult {
  projects: Readonly<Ref<readonly ProjectResponse[]>>;
  isLoading: Readonly<ShallowRef<boolean>>;
  isRefreshing: Readonly<ShallowRef<boolean>>;
  error: Readonly<ShallowRef<string | undefined>>;
  refetch: () => Promise<void>;
}

export function useProjects(options: UseProjectsOptions = {}): UseProjectsResult {
  const projects = ref<ProjectResponse[]>([]);
  const isLoading = shallowRef(false);
  const isRefreshing = shallowRef(false);
  const error = shallowRef<string | undefined>(undefined);

  let requestId = 0;
  let hasLoadedOnce = false;

  const isEnabled = () => toValue(options.enabled) ?? true;

  async function fetchProjects(): Promise<void> {
    if (!isEnabled()) {
      isLoading.value = false;
      isRefreshing.value = false;
      return;
    }

    const currentRequestId = ++requestId;
    const shouldShowInitialLoader = !hasLoadedOnce && projects.value.length === 0;

    if (shouldShowInitialLoader) {
      isLoading.value = true;
    } else {
      isRefreshing.value = true;
    }

    try {
      const response = await apiFetch("/api/projects");
      if (!response.ok) {
        throw new Error(`HTTP ${response.status}`);
      }

      const data = (await response.json()) as ProjectResponse[];
      if (currentRequestId !== requestId) {
        return;
      }

      projects.value = data;
      error.value = undefined;
      hasLoadedOnce = true;
    } catch (fetchError) {
      if (currentRequestId !== requestId) {
        return;
      }

      error.value = fetchError instanceof Error ? fetchError.message : String(fetchError);
      hasLoadedOnce = true;
    } finally {
      if (currentRequestId !== requestId) {
        return;
      }

      isLoading.value = false;
      isRefreshing.value = false;
    }
  }

  watch(
    () => isEnabled(),
    async (enabled) => {
      if (!enabled) {
        isLoading.value = false;
        isRefreshing.value = false;
        return;
      }

      await fetchProjects();
    },
    { immediate: true },
  );

  return {
    projects: readonly(projects),
    isLoading: readonly(isLoading),
    isRefreshing: readonly(isRefreshing),
    error: readonly(error),
    refetch: fetchProjects,
  };
}
