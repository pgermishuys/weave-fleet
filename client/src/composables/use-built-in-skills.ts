import { computed, readonly, shallowRef } from "vue";
import { storeToRefs } from "pinia";
import { apiFetch } from "@/lib/api-client";
import { extractApiError } from "@/lib/api-error";
import type { BuiltInSkill } from "@/lib/skill-versions";
import { useBuiltInSkillsStore } from "@/stores/built-in-skills";

export type { BuiltInSkill } from "@/lib/skill-versions";

const BUILT_IN_SKILLS_PATH = "/api/skills/built-in";

async function errorFrom(response: Response, fallback: string): Promise<string> {
  try {
    const body = (await response.json()) as { error?: unknown };
    return extractApiError(body.error ?? body, fallback);
  } catch {
    return fallback;
  }
}

/**
 * Fleet's built-in skills. Each is off until the user turns it on; sessions started afterwards get the change.
 * One change at a time: the server keeps the choice as one preference, so two at once could lose one.
 */
export function useBuiltInSkills() {
  const store = useBuiltInSkillsStore();
  const { skills, isLoading, error: loadError } = storeToRefs(store);
  const saveError = shallowRef<string | null>(null);
  const savingName = shallowRef<string | null>(null);

  async function setEnabled(name: string, enabled: boolean): Promise<void> {
    if (savingName.value) return;

    savingName.value = name;
    saveError.value = null;
    try {
      const response = await apiFetch(`${BUILT_IN_SKILLS_PATH}/${encodeURIComponent(name)}`, {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ enabled }),
      });
      if (!response.ok) throw new Error(await errorFrom(response, `Couldn't turn ${name} ${enabled ? "on" : "off"}.`));
      store.update((await response.json()) as BuiltInSkill);
    } catch (caught) {
      saveError.value = caught instanceof Error ? caught.message : `Couldn't turn ${name} ${enabled ? "on" : "off"}.`;
    } finally {
      savingName.value = null;
    }
  }

  // Settings always shows the server's current list, even when the conversation loaded it earlier.
  void store.load();

  return {
    skills: readonly(skills),
    isLoading: readonly(isLoading),
    error: computed(() => saveError.value ?? loadError.value),
    savingName: readonly(savingName),
    setEnabled,
  };
}
