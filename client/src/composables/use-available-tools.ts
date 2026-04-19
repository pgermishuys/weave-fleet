import { onMounted, readonly, ref, shallowRef, type Ref, type ShallowRef } from "vue";
import { apiFetch } from "@/lib/api-client";

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
const sharedIsLoading = shallowRef(false);
const sharedError = shallowRef<string | undefined>(undefined);

async function fetchAvailableTools(): Promise<AvailableTool[]> {
  const response = await apiFetch("/api/available-tools");
  if (!response.ok) {
    throw new Error(`Failed to fetch available tools: HTTP ${response.status}`);
  }

  const data = (await response.json()) as { tools?: AvailableTool[] } | AvailableTool[];
  if (Array.isArray(data)) {
    return data;
  }

  return Array.isArray(data.tools) ? data.tools : [];
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
