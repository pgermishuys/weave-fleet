import { defineStore } from "pinia";
import { computed, shallowRef } from "vue";
import { apiFetch } from "@/lib/api-client";
import { extractApiError } from "@/lib/api-error";
import type { BuiltInSkill, BuiltInSkillDetail } from "@/lib/skill-versions";

const PATH = "/api/skills/built-in";

/**
 * Fleet's built-in skills, shared by Settings and the conversation: Settings turns them on and edits them, and a
 * skill row in the conversation offers Improve when the skill is one of these.
 */
export const useBuiltInSkillsStore = defineStore("built-in-skills", () => {
  const skills = shallowRef<BuiltInSkill[]>([]);
  const isLoading = shallowRef(false);
  const hasLoaded = shallowRef(false);
  const error = shallowRef<string | null>(null);

  const names = computed(() => new Set(skills.value.map((skill) => skill.name)));

  async function load(): Promise<void> {
    isLoading.value = true;
    error.value = null;
    try {
      const response = await apiFetch(PATH);
      if (!response.ok) {
        let message = "Couldn't load Fleet's built-in skills.";
        try {
          const body = (await response.json()) as { error?: unknown };
          message = extractApiError(body.error ?? body, message);
        } catch {
          // Not JSON: keep the fallback.
        }
        throw new Error(message);
      }
      const body: unknown = await response.json();
      skills.value = Array.isArray(body) ? (body as BuiltInSkill[]) : [];
      hasLoaded.value = true;
    } catch (caught) {
      error.value = caught instanceof Error ? caught.message : "Couldn't load Fleet's built-in skills.";
    } finally {
      isLoading.value = false;
    }
  }

  /** Loads the list once; later calls reuse it. */
  function ensureLoaded(): void {
    if (!hasLoaded.value && !isLoading.value) void load();
  }

  /** Puts the server's answer for one skill into the list. */
  function update(skill: BuiltInSkill | BuiltInSkillDetail): void {
    const view: BuiltInSkill = {
      name: skill.name,
      description: skill.description,
      enabled: skill.enabled,
      version: skill.version ?? null,
      fleetChanged: skill.fleetChanged ?? false,
      versionCount: "versions" in skill ? skill.versions.length : skill.versionCount ?? 0,
    };
    skills.value = skills.value.some((existing) => existing.name === view.name)
      ? skills.value.map((existing) => (existing.name === view.name ? view : existing))
      : [...skills.value, view];
  }

  function isBuiltIn(name: string): boolean {
    return names.value.has(name);
  }

  return { skills, isLoading, hasLoaded, error, load, ensureLoaded, update, isBuiltIn };
});
