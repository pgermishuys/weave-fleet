import { readonly, ref, shallowRef, type Ref, type ShallowRef } from "vue";
import { api, type components } from "@/api/client";
import { extractApiError } from "@/lib/api-error";

export type SkillCatalogResponse = components["schemas"]["SkillCatalogResponse"];
export type InstallSkillRequest = components["schemas"]["InstallSkillRequest"];
export type InstallSkillResponse = components["schemas"]["InstallSkillResponse"];

/**
 * Map the numeric SkillSource enum from the API to string literals.
 * The API serializes enums as integers, but the frontend uses string literals.
 */
function mapSkillSource(source: number): "GitHub" | "Local" | "Bundled" {
  // Enum values: Bundled = 0, GitHub = 1, Local = 2
  switch (source) {
    case 0:
      return "Bundled";
    case 1:
      return "GitHub";
    case 2:
      return "Local";
    default:
      return "Local"; // fallback
  }
}

export interface CatalogEntry {
  name: string;
  displayName?: string | null;
  description?: string | null;
  source: "GitHub" | "Local" | "Bundled";
  repoUrl?: string | null;
  ref?: string | null;
  localPath?: string | null;
  targetHarnesses: readonly string[];
  author?: string | null;
  version?: string | null;
  tags: readonly string[];
  createdAt?: string | null;
  updatedAt?: string | null;
}

export interface UseSkillCatalogResult {
  catalog: Readonly<Ref<readonly CatalogEntry[]>>;
  isStale: Readonly<Ref<boolean>>;
  cachedAt: Readonly<Ref<string | null>>;
  isLoading: Readonly<ShallowRef<boolean>>;
  error: Readonly<ShallowRef<string | undefined>>;
  fetchCatalog: () => Promise<void>;
  installSkill: (request: InstallSkillRequest) => Promise<InstallSkillResponse | undefined>;
}

export function useSkillCatalog(): UseSkillCatalogResult {
  const catalog = ref<CatalogEntry[]>([]);
  const isStale = ref(false);
  const cachedAt = ref<string | null>(null);
  const isLoading = shallowRef(true);
  const error = shallowRef<string | undefined>(undefined);

  async function fetchCatalog(): Promise<void> {
    try {
      isLoading.value = true;
      error.value = undefined;

      const { data, error: apiError } = await api.GET("/api/skills/catalog");
      if (apiError) {
        throw new Error(extractApiError(apiError, "Failed to fetch skill catalog"));
      }

      if (!data) {
        throw new Error("No data returned");
      }

      catalog.value = (data.entries ?? []).map((entry) => ({
        name: entry.name,
        displayName: entry.displayName,
        description: entry.description,
        source: mapSkillSource(entry.source),
        repoUrl: entry.repoUrl,
        ref: entry.ref,
        localPath: entry.localPath,
        targetHarnesses: entry.targetHarnesses as readonly string[],
        author: entry.author,
        version: entry.version,
        tags: entry.tags as readonly string[],
        createdAt: entry.createdAt,
        updatedAt: entry.updatedAt,
      }));
      isStale.value = data.isStale ?? false;
      cachedAt.value = data.cachedAt ?? null;
    } catch (fetchError) {
      error.value = fetchError instanceof Error ? fetchError.message : "Unknown error";
    } finally {
      isLoading.value = false;
    }
  }

  async function installSkill(
    request: InstallSkillRequest,
  ): Promise<InstallSkillResponse | undefined> {
    try {
      error.value = undefined;

      const { data, error: apiError } = await api.POST("/api/skills/install", {
        body: request,
      });

      if (apiError) {
        throw new Error(extractApiError(apiError, "Failed to install skill"));
      }

      if (!data) {
        throw new Error("No data returned");
      }

      return data;
    } catch (installError) {
      error.value = installError instanceof Error ? installError.message : "Unknown error";
      throw installError;
    }
  }

  void fetchCatalog();

  return {
    catalog: readonly(catalog),
    isStale: readonly(isStale),
    cachedAt: readonly(cachedAt),
    isLoading: readonly(isLoading),
    error: readonly(error),
    fetchCatalog,
    installSkill,
  };
}
