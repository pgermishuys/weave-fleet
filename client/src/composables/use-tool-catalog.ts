import { readonly, ref, shallowRef, type Ref, type ShallowRef } from "vue";
import { api, type components } from "@/api/client";
import { extractApiError } from "@/lib/api-error";

export type ToolCatalogResponse = components["schemas"]["ToolCatalogResponse"];
export type InstallToolRequest = components["schemas"]["InstallToolRequest"];
export type InstallToolResponse = components["schemas"]["InstallToolResponse"];

/**
 * Map the tool type string from the API to string literals.
 * The API returns toolType as a string like "native" or "mcp".
 */
function mapToolType(toolType: string): "native" | "mcp" {
  switch (toolType.toLowerCase()) {
    case "native":
      return "native";
    case "mcp":
      return "mcp";
    default:
      return "native"; // fallback
  }
}

export interface ToolCatalogEntry {
  name: string;
  toolType: "native" | "mcp";
  displayName?: string | null;
  description?: string | null;
  command?: string | null;
  args?: readonly string[] | null;
  env?: Record<string, string> | null;
  repoUrl?: string | null;
  localPath?: string | null;
  author?: string | null;
  version?: string | null;
  tags: readonly string[];
  createdAt?: string | null;
  updatedAt?: string | null;
}

export interface UseToolCatalogResult {
  catalog: Readonly<Ref<readonly ToolCatalogEntry[]>>;
  isStale: Readonly<Ref<boolean>>;
  cachedAt: Readonly<Ref<string | null>>;
  isLoading: Readonly<ShallowRef<boolean>>;
  error: Readonly<ShallowRef<string | undefined>>;
  fetchCatalog: () => Promise<void>;
  installTool: (request: InstallToolRequest) => Promise<InstallToolResponse | undefined>;
}

export function useToolCatalog(): UseToolCatalogResult {
  const catalog = ref<ToolCatalogEntry[]>([]);
  const isStale = ref(false);
  const cachedAt = ref<string | null>(null);
  const isLoading = shallowRef(true);
  const error = shallowRef<string | undefined>(undefined);

  async function fetchCatalog(): Promise<void> {
    try {
      isLoading.value = true;
      error.value = undefined;

      const { data, error: apiError } = await api.GET("/api/tools/catalog");
      if (apiError) {
        throw new Error(extractApiError(apiError, "Failed to fetch tool catalog"));
      }

      if (!data) {
        throw new Error("No data returned");
      }

      catalog.value = (data.entries ?? []).map((entry) => ({
        name: entry.name,
        toolType: mapToolType(entry.toolType),
        displayName: entry.displayName,
        description: entry.description,
        command: entry.command,
        args: entry.args as readonly string[] | null,
        env: entry.env ?? null,
        repoUrl: entry.repoUrl,
        localPath: entry.localPath,
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

  async function installTool(
    request: InstallToolRequest,
  ): Promise<InstallToolResponse | undefined> {
    try {
      error.value = undefined;

      const { data, error: apiError } = await api.POST("/api/tools/install", {
        body: request,
      });

      if (apiError) {
        throw new Error(extractApiError(apiError, "Failed to install tool"));
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
    installTool,
  };
}
