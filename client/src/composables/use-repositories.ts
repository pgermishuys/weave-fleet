import { onMounted, onUnmounted, readonly, ref, shallowRef, type Ref, type ShallowRef } from "vue";
import { apiFetch } from "@/lib/api-client";
import type { RepositoryScanResponse, ScannedRepository } from "@/lib/api-types";

export function groupByRoot(repositories: ScannedRepository[]): Map<string, ScannedRepository[]> {
  const grouped = new Map<string, ScannedRepository[]>();

  for (const repository of repositories) {
    const group = grouped.get(repository.parentRoot) ?? [];
    group.push(repository);
    grouped.set(repository.parentRoot, group);
  }

  for (const [key, group] of grouped) {
    grouped.set(key, [...group].sort((left, right) => left.name.localeCompare(right.name)));
  }

  return grouped;
}

interface UseRepositoriesResult {
  repositories: Readonly<Ref<readonly ScannedRepository[]>>;
  isLoading: Readonly<ShallowRef<boolean>>;
  error: Readonly<ShallowRef<string | null>>;
  scannedAt: Readonly<ShallowRef<number | null>>;
  refresh: () => Promise<void>;
}

const reposBus = new EventTarget();

function broadcastReposUpdate(data: RepositoryScanResponse): void {
  reposBus.dispatchEvent(new CustomEvent<RepositoryScanResponse>("repos-updated", { detail: data }));
}

export function useRepositories(): UseRepositoriesResult {
  const repositories = ref<ScannedRepository[]>([]);
  const isLoading = shallowRef(true);
  const error = shallowRef<string | null>(null);
  const scannedAt = shallowRef<number | null>(null);

  function applyData(data: RepositoryScanResponse): void {
    repositories.value = data.repositories;
    scannedAt.value = data.scannedAt;
  }

  async function loadRepositories(endpoint: string): Promise<void> {
    isLoading.value = true;
    error.value = null;

    try {
      const response = await apiFetch(endpoint, {
        method: endpoint.includes("refresh") ? "POST" : "GET",
      });

      if (!response.ok) {
        const data = (await response.json().catch(() => ({}))) as { error?: string };
        throw new Error(data.error ?? "Failed to load repositories");
      }

      const data = (await response.json()) as RepositoryScanResponse;
      applyData(data);
      broadcastReposUpdate(data);
    } catch (fetchError) {
      error.value = fetchError instanceof Error ? fetchError.message : "Unknown error";
    } finally {
      isLoading.value = false;
    }
  }

  function handleReposUpdated(event: Event): void {
    const data = (event as CustomEvent<RepositoryScanResponse>).detail;
    applyData(data);
  }

  onMounted(() => {
    void loadRepositories("/api/repositories");
    reposBus.addEventListener("repos-updated", handleReposUpdated);
  });

  onUnmounted(() => {
    reposBus.removeEventListener("repos-updated", handleReposUpdated);
  });

  return {
    repositories: readonly(repositories),
    isLoading: readonly(isLoading),
    error: readonly(error),
    scannedAt: readonly(scannedAt),
    refresh: () => loadRepositories("/api/repositories/refresh"),
  };
}
