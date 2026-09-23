import { computed } from "vue";
import { createModelSelectionKey } from "@/composables/use-models";
import { modelFromKey, modelToPath } from "@/lib/agent-model-choice";
import {
  MODEL_ROLES_PREFERENCE_KEY,
  parseModelRoles,
  type ModelRoles,
  type WorkflowModelChoice,
  type WorkflowRole,
} from "@/lib/workflows";
import { usePreferencesStore } from "@/stores/preferences";

/** A model path (`provider/model`) as the model picker's selection key; empty for the default model. */
export function keyFromPath(path: string | null | undefined): string {
  const slash = path?.indexOf("/") ?? -1;
  return path && slash > 0 && slash < path.length - 1 ? createModelSelectionKey(path.slice(0, slash), path.slice(slash + 1)) : "";
}

/** The model picker's selection key as a model path; null for the default model. */
export function pathFromKey(key: string): string | null {
  return modelToPath(modelFromKey(key));
}

/**
 * Which of the user's models does each kind of work, per harness: Settings → Workflows → Model roles. A per-user
 * Fleet preference, not stored in the repo, so a workflow someone shares works on the reader's providers.
 */
export function useModelRoles() {
  const preferencesStore = usePreferencesStore();
  preferencesStore.ensureLoaded();

  const roles = computed<ModelRoles>(() => parseModelRoles(preferencesStore.get(MODEL_ROLES_PREFERENCE_KEY, "")));

  function choiceFor(harnessType: string, role: WorkflowRole): WorkflowModelChoice {
    return roles.value[harnessType]?.[role] ?? { model: null, effort: null };
  }

  async function setChoice(harnessType: string, role: WorkflowRole, choice: WorkflowModelChoice): Promise<void> {
    const next: ModelRoles = { ...roles.value, [harnessType]: { ...roles.value[harnessType], [role]: choice } };
    await preferencesStore.set(MODEL_ROLES_PREFERENCE_KEY, JSON.stringify(next));
  }

  return { roles, choiceFor, setChoice };
}
