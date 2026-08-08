import { readonly, ref, shallowRef, type Ref, type ShallowRef } from "vue";
import { api } from "@/api/client";

export interface InstalledSkill {
  name: string;
  description: string;
  path: string;
  assignedAgents: readonly string[];
}

export interface UseSkillsResult {
  skills: Readonly<Ref<readonly InstalledSkill[]>>;
  isLoading: Readonly<ShallowRef<boolean>>;
  error: Readonly<ShallowRef<string | undefined>>;
  fetchSkills: () => Promise<void>;
  installSkill: (options: { url?: string; content?: string; agents?: string[] }) => Promise<unknown>;
  removeSkill: (name: string) => Promise<void>;
}

export function useSkills(): UseSkillsResult {
  const skills = ref<InstalledSkill[]>([]);
  const isLoading = shallowRef(true);
  const error = shallowRef<string | undefined>(undefined);

  async function fetchSkills(): Promise<void> {
    try {
      isLoading.value = true;
      error.value = undefined;

      const { data, error: apiError } = await api.GET("/api/skills");
      if (apiError) {
        throw new Error(apiError ? String(apiError) : "Failed to fetch skills");
      }

      if (!data) {
        throw new Error("No data returned");
      }

      const json = data as { skills?: InstalledSkill[] };
      skills.value = json.skills ?? [];
    } catch (fetchError) {
      error.value = fetchError instanceof Error ? fetchError.message : "Unknown error";
    } finally {
      isLoading.value = false;
    }
  }

  async function installSkill(options: {
    url?: string;
    content?: string;
    agents?: string[];
  }): Promise<unknown> {
    try {
      error.value = undefined;

      const { data, error: apiError } = await api.POST("/api/skills", {
        body: options as never,
      });

      if (apiError) {
        throw new Error(apiError ? String(apiError) : "Failed to install skill");
      }

      await fetchSkills();
      return data;
    } catch (installError) {
      error.value = installError instanceof Error ? installError.message : "Unknown error";
      throw installError;
    }
  }

  async function removeSkill(name: string): Promise<void> {
    try {
      error.value = undefined;

      const { error: apiError } = await api.DELETE("/api/skills/{name}", {
        params: { path: { name } },
      });

      if (apiError) {
        throw new Error(apiError ? String(apiError) : "Failed to remove skill");
      }

      await fetchSkills();
    } catch (removeError) {
      error.value = removeError instanceof Error ? removeError.message : "Unknown error";
      throw removeError;
    }
  }

  void fetchSkills();

  return {
    skills: readonly(skills),
    isLoading: readonly(isLoading),
    error: readonly(error),
    fetchSkills,
    installSkill,
    removeSkill,
  };
}
