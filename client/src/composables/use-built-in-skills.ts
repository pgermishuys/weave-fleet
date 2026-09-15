import { readonly, shallowRef } from "vue";
import { apiFetch } from "@/lib/api-client";
import { extractApiError } from "@/lib/api-error";

/** A skill that ships with Fleet, and whether the user turned it on for their sessions. */
export interface BuiltInSkill {
  name: string;
  description: string;
  enabled: boolean;
}

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
  const skills = shallowRef<BuiltInSkill[]>([]);
  const isLoading = shallowRef(true);
  const error = shallowRef<string | null>(null);
  const savingName = shallowRef<string | null>(null);

  async function load(): Promise<void> {
    isLoading.value = true;
    error.value = null;
    try {
      const response = await apiFetch(BUILT_IN_SKILLS_PATH);
      if (!response.ok) throw new Error(await errorFrom(response, "Couldn't load Fleet's built-in skills."));
      const body: unknown = await response.json();
      skills.value = Array.isArray(body) ? (body as BuiltInSkill[]) : [];
    } catch (caught) {
      error.value = caught instanceof Error ? caught.message : "Couldn't load Fleet's built-in skills.";
    } finally {
      isLoading.value = false;
    }
  }

  async function setEnabled(name: string, enabled: boolean): Promise<void> {
    if (savingName.value) return;

    savingName.value = name;
    error.value = null;
    try {
      const response = await apiFetch(`${BUILT_IN_SKILLS_PATH}/${encodeURIComponent(name)}`, {
        method: "PUT",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ enabled }),
      });
      if (!response.ok) throw new Error(await errorFrom(response, `Couldn't turn ${name} ${enabled ? "on" : "off"}.`));
      const updated = (await response.json()) as BuiltInSkill;
      skills.value = skills.value.map((skill) => (skill.name === updated.name ? updated : skill));
    } catch (caught) {
      error.value = caught instanceof Error ? caught.message : `Couldn't turn ${name} ${enabled ? "on" : "off"}.`;
    } finally {
      savingName.value = null;
    }
  }

  void load();

  return {
    skills: readonly(skills),
    isLoading: readonly(isLoading),
    error: readonly(error),
    savingName: readonly(savingName),
    setEnabled,
  };
}
