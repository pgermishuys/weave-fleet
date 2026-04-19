import { readonly, ref, shallowRef, type Ref, type ShallowRef } from "vue";
import { apiFetch } from "@/lib/api-client";

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

      const response = await apiFetch("/api/skills");
      if (!response.ok) {
        throw new Error(`Failed to fetch skills: ${response.status}`);
      }

      const json = (await response.json()) as { skills?: InstalledSkill[] };
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

      const response = await apiFetch("/api/skills", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(options),
      });

      if (!response.ok) {
        const json = (await response.json().catch(() => ({}))) as { error?: string };
        throw new Error(json.error ?? `Failed to install skill: ${response.status}`);
      }

      const result = await response.json();
      await fetchSkills();
      return result;
    } catch (installError) {
      error.value = installError instanceof Error ? installError.message : "Unknown error";
      throw installError;
    }
  }

  async function removeSkill(name: string): Promise<void> {
    try {
      error.value = undefined;

      const response = await apiFetch(`/api/skills/${encodeURIComponent(name)}`, {
        method: "DELETE",
      });

      if (!response.ok) {
        const json = (await response.json().catch(() => ({}))) as { error?: string };
        throw new Error(json.error ?? `Failed to remove skill: ${response.status}`);
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
