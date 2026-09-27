import { onMounted, onUnmounted, readonly, ref, shallowRef, type Ref, type ShallowRef } from "vue";
import type { RepositoryScanResponse, ScannedRepository } from "@/api/client";
import { useMachineTarget } from "@/lib/machine-target";
import { readSaved, writeSaved } from "@/lib/saved-per-machine";

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
const SAVED_KIND = "repositories";

interface ReposUpdate {
  machineKey: string;
  data: RepositoryScanResponse;
}

function broadcastReposUpdate(update: ReposUpdate): void {
  reposBus.dispatchEvent(new CustomEvent<ReposUpdate>("repos-updated", { detail: update }));
}

/**
 * The repositories Fleet found on the machine (the one the page asks: see `useMachineTarget`). The list saved on the
 * last visit shows at once, so the new-session box can pick its folder before the machine answers.
 */
export function useRepositories(): UseRepositoriesResult {
  const { api, key: machineKey } = useMachineTarget();
  const saved = readSaved<RepositoryScanResponse>(SAVED_KIND, machineKey);
  const repositories = ref<ScannedRepository[]>(Array.isArray(saved?.repositories) ? saved.repositories : []);
  const isLoading = shallowRef(false);
  const error = shallowRef<string | null>(null);
  const scannedAt = shallowRef<number | null>(typeof saved?.scannedAt === "number" ? saved.scannedAt : null);

  function applyData(data: RepositoryScanResponse): void {
    repositories.value = data.repositories;
    scannedAt.value = data.scannedAt;
  }

  async function loadRepositories(endpoint: string): Promise<void> {
    isLoading.value = true;
    error.value = null;

    try {
      const { data, error: apiError } = endpoint.includes("refresh")
        ? await api.POST("/api/repositories/refresh", {})
        : await api.GET("/api/repositories", {});

      if (apiError) {
        throw new Error(String(apiError));
      }

      if (!data) {
        throw new Error("No data returned");
      }

      const responseData = data as RepositoryScanResponse;
      applyData(responseData);
      writeSaved(SAVED_KIND, machineKey, responseData);
      broadcastReposUpdate({ machineKey, data: responseData });
    } catch (fetchError) {
      error.value = fetchError instanceof Error ? fetchError.message : "Unknown error";
    } finally {
      isLoading.value = false;
    }
  }

  function handleReposUpdated(event: Event): void {
    const update = (event as CustomEvent<ReposUpdate>).detail;
    if (update.machineKey === machineKey) applyData(update.data);
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
