import { onMounted, readonly, ref, shallowRef, type Ref, type ShallowRef } from "vue";
import { api } from "@/api/client";
import { extractApiError } from "@/lib/api-error";

export interface AvailableTool {
  id: string;
  label: string;
  iconName: string;
  category: "editor" | "terminal" | "explorer";
}

export interface UseAvailableToolsResult {
  tools: Readonly<Ref<readonly AvailableTool[]>>;
  isLoading: Readonly<ShallowRef<boolean>>;
  error: Readonly<ShallowRef<string | undefined>>;
  refetch: () => Promise<void>;
}

let moduleCache: AvailableTool[] | null = null;
let moduleFetchPromise: Promise<AvailableTool[]> | null = null;
const sharedTools = ref<AvailableTool[]>([]);
const sharedIsLoading = shallowRef(true);
const sharedError = shallowRef<string | undefined>(undefined);

async function fetchAvailableTools(): Promise<AvailableTool[]> {
  const { data, error: apiError } = await api.GET("/api/available-tools");
  if (apiError) {
    throw new Error(extractApiError(apiError, "Failed to fetch available tools"));
  }

  if (!data) {
    throw new Error("No data returned");
  }

  const result = data as { tools?: AvailableTool[] } | AvailableTool[];
  if (Array.isArray(result)) {
    return result;
  }

  return Array.isArray(result.tools) ? result.tools : [];
}

async function loadTools(): Promise<void> {
  if (moduleCache) {
    sharedTools.value = moduleCache;
  }

  sharedIsLoading.value = !moduleCache;
  sharedError.value = undefined;

  if (!moduleFetchPromise) {
    moduleFetchPromise = fetchAvailableTools();
  }

  try {
    const tools = await moduleFetchPromise;
    moduleCache = tools;
    sharedTools.value = tools;
    sharedError.value = undefined;
  } catch (fetchError) {
    sharedError.value = fetchError instanceof Error ? fetchError.message : "Failed to load tools";
  } finally {
    sharedIsLoading.value = false;
    moduleFetchPromise = null;
  }
}

// Eagerly start fetching tools on module load so they're ready when UI renders
void loadTools();

export function useAvailableTools(): UseAvailableToolsResult {
  onMounted(() => {
    void loadTools();
  });

  return {
    tools: readonly(sharedTools),
    isLoading: readonly(sharedIsLoading),
    error: readonly(sharedError),
    refetch: loadTools,
  };
}

export function getToolsByCategory(
  tools: readonly AvailableTool[] | undefined | null,
  category: AvailableTool["category"],
): AvailableTool[] {
  if (!tools) {
    return [];
  }

  return tools.filter((tool) => tool.category === category);
}

export function getDefaultTool(available: readonly AvailableTool[]): string {
  if (available.length === 0) {
    return "vscode";
  }

  const firstEditor = available.find((tool) => tool.category === "editor");
  return firstEditor?.id ?? available[0].id;
}

export function getToolLabel(toolId: string, available: readonly AvailableTool[]): string {
  const tool = available.find((entry) => entry.id === toolId);
  return tool?.label ?? `${toolId.charAt(0).toUpperCase()}${toolId.slice(1)}`;
}

export function invalidateToolsCache(): void {
  moduleCache = null;
  moduleFetchPromise = null;
  sharedTools.value = [];
  sharedError.value = undefined;
}
