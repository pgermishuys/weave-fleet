import { computed, readonly, ref, shallowRef, type ComputedRef, type ShallowRef } from "vue";
import { apiFetch } from "@/lib/api-client";

export interface InstalledSkill {
  name: string;
  description: string;
  path: string;
  assignedAgents: string[];
}

export interface WeaveConfig {
  agents?: Record<string, { skills?: string[]; model?: string }>;
}

export interface ProviderModelInfo {
  id: string;
  name: string;
}

export interface ProviderStatus {
  id: string;
  name: string;
  connected: boolean;
  authType: "api" | "oauth" | "wellknown" | null;
  models: ProviderModelInfo[];
}

interface ConfigData {
  userConfig: WeaveConfig;
  installedSkills: InstalledSkill[];
  paths: { userConfig: string; skillsDir: string };
  connectedProviders?: ProviderStatus[];
}

export interface UseConfigResult {
  config: ComputedRef<WeaveConfig | null>;
  installedSkills: ComputedRef<readonly InstalledSkill[]>;
  paths: ComputedRef<{ userConfig: string; skillsDir: string } | null>;
  providers: ComputedRef<readonly ProviderStatus[]>;
  isLoading: Readonly<ShallowRef<boolean>>;
  error: Readonly<ShallowRef<string | undefined>>;
  fetchConfig: () => Promise<void>;
  updateConfig: (config: WeaveConfig) => Promise<void>;
}

export function useConfig(): UseConfigResult {
  const data = ref<ConfigData | null>(null);
  const isLoading = shallowRef(true);
  const error = shallowRef<string | undefined>(undefined);

  async function fetchConfig(): Promise<void> {
    try {
      isLoading.value = true;
      error.value = undefined;

      const response = await apiFetch("/api/config");
      if (!response.ok) {
        throw new Error(`Failed to fetch config: ${response.status}`);
      }

      data.value = (await response.json()) as ConfigData;
    } catch (fetchError) {
      error.value = fetchError instanceof Error ? fetchError.message : "Unknown error";
    } finally {
      isLoading.value = false;
    }
  }

  async function updateConfig(config: WeaveConfig): Promise<void> {
    try {
      error.value = undefined;

      const response = await apiFetch("/api/config", {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(config),
      });

      if (!response.ok) {
        throw new Error(`Failed to update config: ${response.status}`);
      }

      await fetchConfig();
    } catch (updateError) {
      error.value = updateError instanceof Error ? updateError.message : "Unknown error";
      throw updateError;
    }
  }

  void fetchConfig();

  return {
    config: computed(() => data.value?.userConfig ?? null),
    installedSkills: computed(() => data.value?.installedSkills ?? []),
    paths: computed(() => data.value?.paths ?? null),
    providers: computed(() => data.value?.connectedProviders ?? []),
    isLoading: readonly(isLoading),
    error: readonly(error),
    fetchConfig,
    updateConfig,
  };
}
