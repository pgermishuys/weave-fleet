import { onMounted, readonly, ref, shallowRef, type Ref, type ShallowRef } from "vue";
import { liveTarget, useMachineTarget, type MachineTarget } from "@/lib/machine-target";
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

/** What one machine offers: tools are found on the machine that opens them, so each machine has its own answer. */
interface MachineTools {
  cache: AvailableTool[] | null;
  fetchPromise: Promise<AvailableTool[]> | null;
  tools: Ref<AvailableTool[]>;
  isLoading: ShallowRef<boolean>;
  error: ShallowRef<string | undefined>;
}

const perMachine = new Map<string, MachineTools>();

function stateFor(key: string): MachineTools {
  let state = perMachine.get(key);
  if (!state) {
    state = { cache: null, fetchPromise: null, tools: ref<AvailableTool[]>([]), isLoading: shallowRef(true), error: shallowRef<string | undefined>(undefined) };
    perMachine.set(key, state);
  }
  return state;
}

async function fetchAvailableTools(target: MachineTarget): Promise<AvailableTool[]> {
  const { data, error: apiError } = await target.api.GET("/api/available-tools");
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

async function loadTools(target: MachineTarget): Promise<void> {
  const state = stateFor(target.key);
  if (state.cache) {
    state.tools.value = state.cache;
  }

  state.isLoading.value = !state.cache;
  state.error.value = undefined;

  if (!state.fetchPromise) {
    state.fetchPromise = fetchAvailableTools(target);
  }

  try {
    const tools = await state.fetchPromise;
    state.cache = tools;
    state.tools.value = tools;
    state.error.value = undefined;
  } catch (fetchError) {
    state.error.value = fetchError instanceof Error ? fetchError.message : "Failed to load tools";
  } finally {
    state.isLoading.value = false;
    state.fetchPromise = null;
  }
}

// Eagerly start fetching the live machine's tools on module load so they're ready when UI renders
void loadTools(liveTarget());

/** The tools the open session's machine offers (the live machine's outside a session). */
export function useAvailableTools(): UseAvailableToolsResult {
  const target = useMachineTarget();
  const state = stateFor(target.key);
  onMounted(() => {
    void loadTools(target);
  });

  return {
    tools: readonly(state.tools),
    isLoading: readonly(state.isLoading),
    error: readonly(state.error),
    refetch: () => loadTools(target),
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
  for (const state of perMachine.values()) {
    state.cache = null;
    state.fetchPromise = null;
    state.tools.value = [];
    state.error.value = undefined;
  }
}
