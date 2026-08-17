import { readonly, ref, shallowRef, type Ref, type ShallowRef } from "vue";
import { api, type components } from "@/api/client";
import { extractApiError } from "@/lib/api-error";

export type SkillManifestEntry = components["schemas"]["SkillManifestEntry"];
export type UpdateCheckResponse = components["schemas"]["UpdateCheckResponse"];
export type UpdateSkillResponse = components["schemas"]["UpdateSkillResponse"];
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

/**
 * Map string literal SkillSource to numeric enum for API requests.
 */
function mapSkillSourceToNumber(source: "GitHub" | "Local" | "Bundled"): number {
  switch (source) {
    case "Bundled":
      return 0;
    case "GitHub":
      return 1;
    case "Local":
      return 2;
  }
}

export interface InstalledSkill {
  name: string;
  source: "GitHub" | "Local" | "Bundled";
  repoUrl?: string | null;
  ref?: string | null;
  localPath?: string | null;
  targetHarnesses: readonly string[];
  installedAt: string;
  updatedAt: string;
  updateAvailable?: boolean;
  updateCheckError?: string;
  // Computed/derived properties for backward compatibility
  description?: string;
  path?: string;
}

export interface UseSkillsResult {
  skills: Readonly<Ref<readonly InstalledSkill[]>>;
  isLoading: Readonly<ShallowRef<boolean>>;
  error: Readonly<ShallowRef<string | undefined>>;
  fetchSkills: () => Promise<void>;
  checkUpdate: (name: string) => Promise<UpdateCheckResponse | undefined>;
  updateSkill: (name: string) => Promise<UpdateSkillResponse | undefined>;
  removeSkill: (name: string) => Promise<void>;
  installSkill: (options: { url?: string; content?: string }) => Promise<InstallSkillResponse | undefined>;
}

export function useSkills(): UseSkillsResult {
  const skills = ref<InstalledSkill[]>([]);
  const isLoading = shallowRef(true);
  const error = shallowRef<string | undefined>(undefined);

  async function fetchSkills(): Promise<void> {
    try {
      isLoading.value = true;
      error.value = undefined;

      const { data, error: apiError } = await api.GET("/api/skills/manifest");
      if (apiError) {
        throw new Error(extractApiError(apiError, "Failed to fetch skills"));
      }

      if (!data) {
        throw new Error("No data returned");
      }

      skills.value = (data.skills ?? []).map((skill) => ({
        name: skill.name,
        source: mapSkillSource(skill.source),
        repoUrl: skill.repoUrl,
        ref: skill.ref,
        localPath: skill.localPath,
        targetHarnesses: skill.targetHarnesses as readonly string[],
        installedAt: skill.installedAt,
        updatedAt: skill.updatedAt,
        // Backward compatibility: derive description and path
        description: mapSkillSource(skill.source) === "GitHub" ? skill.repoUrl ?? undefined : undefined,
        path:
          mapSkillSource(skill.source) === "Local"
            ? skill.localPath ?? undefined
            : mapSkillSource(skill.source) === "GitHub"
              ? skill.repoUrl ?? undefined
              : undefined,
      }));
    } catch (fetchError) {
      error.value = fetchError instanceof Error ? fetchError.message : "Unknown error";
    } finally {
      isLoading.value = false;
    }
  }

  async function checkUpdate(name: string): Promise<UpdateCheckResponse | undefined> {
    try {
      error.value = undefined;

      const { data, error: apiError } = await api.GET("/api/skills/{name}/update-check", {
        params: { path: { name } },
      });

      if (apiError) {
        const errorMsg = extractApiError(apiError, "Failed to check for updates");
        // Update the skill in the list with the error
        const skillIndex = skills.value.findIndex((s) => s.name === name);
        if (skillIndex !== -1) {
          const updatedSkills = [...skills.value];
          updatedSkills[skillIndex] = {
            ...updatedSkills[skillIndex],
            updateCheckError: errorMsg,
          };
          skills.value = updatedSkills;
        }
        throw new Error(errorMsg);
      }

      if (!data) {
        throw new Error("No data returned");
      }

      // Update the skill in the list with update availability
      const skillIndex = skills.value.findIndex((s) => s.name === name);
      if (skillIndex !== -1) {
        const updatedSkills = [...skills.value];
        updatedSkills[skillIndex] = {
          ...updatedSkills[skillIndex],
          updateAvailable: data.updateAvailable,
          updateCheckError: undefined,
        };
        skills.value = updatedSkills;
      }

      return data;
    } catch (updateCheckError) {
      error.value = updateCheckError instanceof Error ? updateCheckError.message : "Unknown error";
      return undefined;
    }
  }

  async function updateSkill(name: string): Promise<UpdateSkillResponse | undefined> {
    try {
      error.value = undefined;

      const { data, error: apiError } = await api.POST("/api/skills/{name}/update", {
        params: { path: { name } },
      });

      if (apiError) {
        throw new Error(extractApiError(apiError, "Failed to update skill"));
      }

      if (!data) {
        throw new Error("No data returned");
      }

      // Refresh the skills list after update
      await fetchSkills();

      return data;
    } catch (updateError) {
      error.value = updateError instanceof Error ? updateError.message : "Unknown error";
      throw updateError;
    }
  }

  async function removeSkill(name: string): Promise<void> {
    try {
      error.value = undefined;

      const { error: apiError } = await api.DELETE("/api/skills/{name}", {
        params: { path: { name } },
      });

      if (apiError) {
        throw new Error(extractApiError(apiError, "Failed to remove skill"));
      }

      await fetchSkills();
    } catch (removeError) {
      error.value = removeError instanceof Error ? removeError.message : "Unknown error";
      throw removeError;
    }
  }

  /**
   * Parse GitHub browse URLs:
   * https://github.com/owner/repo → repo root
   * https://github.com/owner/repo/tree/branch → repo at branch
   * https://github.com/owner/repo/tree/branch/path/to/skill → subdirectory
   */
  function parseGitHubUrl(url: string): { repoUrl: string; ref: string | null; subPath: string | null; name: string } | null {
    const match = url.match(/^https?:\/\/github\.com\/([^/]+)\/([^/]+?)(?:\.git)?(?:\/tree\/([^/]+)(\/.*)?)?$/);
    if (!match) return null;
    const [, owner, repo, ref, pathPart] = match;
    const subPath = pathPart ? pathPart.replace(/^\//, "") : null;
    // Skill name: use last segment of subPath if present, otherwise repo name
    const name = subPath ? subPath.split("/").filter(Boolean).pop() ?? repo : repo;
    return {
      repoUrl: `https://github.com/${owner}/${repo}.git`,
      ref: ref ?? null,
      subPath,
      name,
    };
  }

  /**
   * Legacy method for backward compatibility with existing components.
   * For new code, use `useSkillCatalog().installSkill()` instead.
   * @deprecated Use useSkillCatalog().installSkill() for new code
   */
  async function installSkill(options: {
    url?: string;
    content?: string;
  }): Promise<InstallSkillResponse | undefined> {
    try {
      error.value = undefined;

      // Parse the URL to extract name and determine source
      const url = options.url?.trim();
      if (!url) {
        throw new Error("URL is required");
      }

      const isGitHub = url.includes("github.com");
      let request: InstallSkillRequest;

      if (isGitHub) {
        const parsed = parseGitHubUrl(url);
        if (!parsed) {
          throw new Error("Invalid GitHub URL format. Expected: https://github.com/owner/repo or https://github.com/owner/repo/tree/branch/path");
        }
        request = {
          name: parsed.name,
          source: mapSkillSourceToNumber("GitHub"),
          repoUrl: parsed.repoUrl,
          ref: parsed.ref,
          localPath: null,
          targetHarnesses: null,
          subPath: null,
        };
      } else {
        const name = url.split("/").filter(Boolean).pop() ?? "unknown-skill";
        request = {
          name,
          source: mapSkillSourceToNumber("Local"),
          repoUrl: null,
          ref: null,
          localPath: url,
          targetHarnesses: null,
          subPath: null,
        };
      }

      const { data, error: apiError } = await api.POST("/api/skills/install", {
        body: request,
      });

      if (apiError) {
        throw new Error(extractApiError(apiError, "Failed to install skill"));
      }

      if (!data) {
        throw new Error("No data returned");
      }

      // Refresh the skills list after install
      await fetchSkills();

      return data;
    } catch (installError) {
      error.value = installError instanceof Error ? installError.message : "Unknown error";
      throw installError;
    }
  }

  void fetchSkills();

  return {
    skills: readonly(skills),
    isLoading: readonly(isLoading),
    error: readonly(error),
    fetchSkills,
    checkUpdate,
    updateSkill,
    removeSkill,
    installSkill,
  };
}
