import { readonly, ref, shallowRef, type Ref, type ShallowRef } from "vue";
import { api, type components } from "@/api/client";
import { extractApiError } from "@/lib/api-error";

export type ToolDto = components["schemas"]["ToolDto"];
export type ToolListResponse = components["schemas"]["ToolListResponse"];
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

export interface InstalledTool {
  name: string;
  toolType: "native" | "mcp";
  displayName?: string | null;
  description?: string | null;
  command?: string | null;
  args?: readonly string[] | null;
  env?: Record<string, string> | null;
  repoUrl?: string | null;
  localPath?: string | null;
  installedAt: string;
  updatedAt: string;
}

export interface UseToolsResult {
  tools: Readonly<Ref<readonly InstalledTool[]>>;
  isLoading: Readonly<ShallowRef<boolean>>;
  error: Readonly<ShallowRef<string | undefined>>;
  fetchTools: () => Promise<void>;
  removeTool: (name: string) => Promise<void>;
  installTool: (request: InstallToolRequest) => Promise<InstallToolResponse | undefined>;
}

export function useTools(): UseToolsResult {
  const tools = ref<InstalledTool[]>([]);
  const isLoading = shallowRef(true);
  const error = shallowRef<string | undefined>(undefined);

  async function fetchTools(): Promise<void> {
    try {
      isLoading.value = true;
      error.value = undefined;

      const { data, error: apiError } = await api.GET("/api/tools");
      if (apiError) {
        throw new Error(extractApiError(apiError, "Failed to fetch tools"));
      }

      if (!data) {
        throw new Error("No data returned");
      }

      tools.value = (data.tools ?? []).map((tool) => ({
        name: tool.name,
        toolType: mapToolType(tool.toolType),
        displayName: tool.displayName,
        description: tool.description,
        command: tool.command,
        args: tool.args as readonly string[] | null,
        env: tool.env ?? null,
        repoUrl: tool.repoUrl,
        localPath: tool.localPath,
        installedAt: tool.installedAt,
        updatedAt: tool.updatedAt,
      }));
    } catch (fetchError) {
      error.value = fetchError instanceof Error ? fetchError.message : "Unknown error";
    } finally {
      isLoading.value = false;
    }
  }

  async function removeTool(name: string): Promise<void> {
    try {
      error.value = undefined;

      const { error: apiError } = await api.DELETE("/api/tools/{name}", {
        params: { path: { name } },
      });

      if (apiError) {
        throw new Error(extractApiError(apiError, "Failed to remove tool"));
      }

      await fetchTools();
    } catch (removeError) {
      error.value = removeError instanceof Error ? removeError.message : "Unknown error";
      throw removeError;
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

      // Refresh the tools list after install
      await fetchTools();

      return data;
    } catch (installError) {
      error.value = installError instanceof Error ? installError.message : "Unknown error";
      throw installError;
    }
  }

  void fetchTools();

  return {
    tools: readonly(tools),
    isLoading: readonly(isLoading),
    error: readonly(error),
    fetchTools,
    removeTool,
    installTool,
  };
}
