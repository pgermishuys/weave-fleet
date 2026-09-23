<script setup lang="ts">
import { computed, onMounted, shallowRef } from "vue";
import { Check, LoaderCircle } from "lucide-vue-next";
import ModelSelector from "@/components/session/ModelSelector.vue";
import SelectorDropdown from "@/components/session/SelectorDropdown.vue";
import { useEnabledHarnesses } from "@/composables/use-enabled-harnesses";
import { useHarnessCatalog } from "@/composables/use-harness-catalog";
import { keyFromPath, pathFromKey, useModelRoles } from "@/composables/use-model-roles";
import { useWorkflowsFeature } from "@/composables/use-workflows-feature";
import { useWorkflowsNav } from "@/composables/use-workflows-nav";
import { describeDefaults } from "@/lib/agent-model-choice";
import { ROLE_DESCRIPTIONS, ROLE_NAMES, WORKFLOW_ROLES, type WorkflowRole } from "@/lib/workflows";
import { usePreferencesStore } from "@/stores/preferences";

const preferencesStore = usePreferencesStore();
const { isWorkflowsEnabled, setWorkflowsEnabled } = useWorkflowsFeature();
const { enabledHarnesses, defaultHarnessType } = useEnabledHarnesses();
const { choiceFor, setChoice } = useModelRoles();
const nav = useWorkflowsNav();

const isSaving = shallowRef(false);
const savingRole = shallowRef<WorkflowRole | null>(null);

async function toggle(): Promise<void> {
  isSaving.value = true;
  try {
    await setWorkflowsEnabled(!isWorkflowsEnabled.value);
  } finally {
    isSaving.value = false;
  }
}

/** Roles are mapped per harness: the same model has a different id on each. */
const harnesses = computed(() => enabledHarnesses.value.filter((harness) => harness.capabilities.supportsWorkflowSteps));
const harnessChoice = shallowRef<string | null>(null);
const harnessType = computed(() => harnessChoice.value
  ?? (harnesses.value.some((h) => h.type === defaultHarnessType.value) ? defaultHarnessType.value : harnesses.value[0]?.type ?? ""));

const { catalog, models, isSupported } = useHarnessCatalog(
  computed(() => (isWorkflowsEnabled.value ? harnessType.value : "")),
  nav.repositoryPath,
);
const defaultLabels = computed(() => describeDefaults(isSupported.value ? catalog.value : null, { agent: "", model: "" }));

function modelKey(role: WorkflowRole): string {
  return keyFromPath(choiceFor(harnessType.value, role).model);
}

async function pickModel(role: WorkflowRole, key: string): Promise<void> {
  savingRole.value = role;
  try {
    // A new model starts on its own default effort.
    await setChoice(harnessType.value, role, { model: pathFromKey(key), effort: null });
  } finally {
    savingRole.value = null;
  }
}

async function pickEffort(role: WorkflowRole, effort: string): Promise<void> {
  await setChoice(harnessType.value, role, { ...choiceFor(harnessType.value, role), effort: effort || null });
}

/** The efforts the role's model offers; none when it's the default model or the model has none. */
function effortItems(role: WorkflowRole) {
  const key = modelKey(role);
  const variants = models.value.find((model) => model.selectionKey === key)?.variants ?? [];
  return [
    { id: "", label: "Effort: default", description: "The model's own effort" },
    ...variants.map((variant) => ({ id: variant, label: `Effort: ${variant}` })),
  ];
}

/** The steps that ask for each role, in the workflows Fleet can see from here. */
function usedBy(role: WorkflowRole): string[] {
  const titles = new Set<string>();
  for (const workflow of nav.library.value?.workflows ?? []) {
    for (const step of workflow.steps) {
      if (step.kind === "agent" && step.model === role) titles.add(step.title);
    }
  }
  return [...titles];
}

onMounted(() => {
  if (!nav.library.value && isWorkflowsEnabled.value) void nav.reload();
});
</script>

<template>
  <div class="flex flex-col gap-4">
    <section
      class="rounded-card border border-border bg-card-bg p-6 shadow-sm"
      data-testid="workflows-switch-card"
    >
      <div class="flex items-start justify-between gap-4">
        <div>
          <h2 class="flex items-center gap-2 text-lg font-semibold text-text">
            Workflows
            <span class="rounded-full border border-border px-2 py-px text-[0.7rem] font-medium uppercase tracking-wide text-muted">
              Experimental
            </span>
          </h2>
          <p class="mt-1 text-sm text-muted">
            Show Workflows in the rail, and let Fleet run workflows: it moves between steps and stops where a step
            asks you. Off: no runner, no step tool, nothing changes.
          </p>
        </div>

        <div class="flex items-center gap-2">
          <LoaderCircle
            v-if="isSaving"
            :size="16"
            class="animate-spin text-muted"
            aria-hidden="true"
          />
          <button
            type="button"
            role="switch"
            :aria-checked="isWorkflowsEnabled"
            :disabled="preferencesStore.isLoading || isSaving"
            aria-label="Enable workflows"
            data-testid="workflows-switch"
            class="relative inline-flex h-6 w-11 shrink-0 cursor-pointer rounded-full border-2 border-transparent transition-colors duration-200 ease-in-out focus:outline-none focus:ring-2 focus:ring-accent focus:ring-offset-2 focus:ring-offset-main-bg disabled:cursor-not-allowed disabled:opacity-60"
            :class="isWorkflowsEnabled ? 'bg-accent' : 'bg-border'"
            @click="toggle"
          >
            <span
              class="pointer-events-none inline-block h-5 w-5 rounded-full bg-white shadow ring-0 transition duration-200 ease-in-out"
              :class="isWorkflowsEnabled ? 'translate-x-5' : 'translate-x-0'"
            />
          </button>
        </div>
      </div>
    </section>

    <section
      v-if="isWorkflowsEnabled"
      class="rounded-card border border-border bg-card-bg p-6 shadow-sm"
      data-testid="workflows-roles-card"
    >
      <div class="flex items-start justify-between gap-4">
        <div>
          <h2 class="text-lg font-semibold text-text">
            Model roles
          </h2>
          <p class="mt-1 text-sm text-muted">
            Workflow steps ask for a role, not a model, so a workflow file someone shares works on your providers.
            Pick which of your models does each kind of work. Kept in Fleet for you, not in the repo.
          </p>
        </div>
        <div
          v-if="harnesses.length > 1"
          class="wf-seg"
          role="group"
          aria-label="Harness"
        >
          <button
            v-for="harness in harnesses"
            :key="harness.type"
            type="button"
            :aria-pressed="harness.type === harnessType"
            @click="harnessChoice = harness.type"
          >
            {{ harness.displayName }}
          </button>
        </div>
      </div>

      <p
        v-if="harnesses.length === 0"
        class="mt-4 text-sm text-muted"
      >
        Workflows run on OpenCode and OpenCode 2. Turn one of them on in Settings → Harnesses.
      </p>

      <template v-else>
        <div
          v-for="role in WORKFLOW_ROLES"
          :key="role"
          class="wf-role"
          :data-testid="`workflows-role-${role}`"
        >
          <div>
            <div class="text-sm font-medium text-text">
              {{ ROLE_NAMES[role] }}
            </div>
            <div class="text-xs text-muted">
              {{ ROLE_DESCRIPTIONS[role] }}
            </div>
          </div>
          <div class="wf-role__controls">
            <ModelSelector
              :model-value="modelKey(role)"
              :models="models"
              :default-label="defaultLabels.modelLabel === 'Default' ? 'Default model' : `Default model · ${defaultLabels.modelLabel.replace(/^Default \(|\)$/g, '')}`"
              default-description="The model the session composer uses when you don't pick one"
              :disabled="!isSupported || savingRole === role"
              :test-id="`workflows-role-model-${role}`"
              @update:model-value="pickModel(role, $event)"
            />
            <SelectorDropdown
              :model-value="choiceFor(harnessType, role).effort ?? ''"
              label="Effort selector"
              :items="effortItems(role)"
              :disabled="effortItems(role).length < 2"
              @update:model-value="pickEffort(role, $event)"
            />
          </div>
          <div class="wf-role__used">
            Used by
            <template v-if="usedBy(role).length">
              <span
                v-for="title in usedBy(role)"
                :key="title"
                class="wf-role__step"
              >{{ title }}</span>
            </template>
            <template v-else>
              no steps yet
            </template>
          </div>
        </div>

        <p class="wf-ok">
          <Check aria-hidden="true" />
          Checked when a run starts. If a role's model isn't available, for example because its provider signed out,
          the run doesn't start and names the step.
        </p>
        <p class="wf-ok wf-ok--plain">
          A run can use different models from the Models menu in its Run box without changing these.
        </p>
      </template>
    </section>
  </div>
</template>

<style scoped>
.wf-seg {
  display: inline-flex;
  flex-shrink: 0;
  padding: 2px;
  border: 1px solid var(--border);
  border-radius: var(--radius-btn);
}

.wf-seg button {
  padding: 3px 10px;
  border: 0;
  border-radius: calc(var(--radius-btn) - 2px);
  background: transparent;
  color: var(--muted);
  cursor: pointer;
  font-size: 12px;
}

.wf-seg button[aria-pressed="true"] {
  background: color-mix(in srgb, var(--text) 9%, transparent);
  color: var(--text);
  font-weight: 500;
}

.wf-role {
  display: grid;
  grid-template-columns: minmax(0, 1fr) auto;
  gap: 6px 16px;
  align-items: center;
  margin-top: 14px;
  padding-top: 14px;
  border-top: 1px solid var(--border);
}

.wf-role__controls {
  display: flex;
  align-items: center;
  gap: 4px;
}

.wf-role__used {
  display: flex;
  flex-wrap: wrap;
  grid-column: 1 / -1;
  align-items: center;
  gap: 6px;
  color: var(--muted);
  font-size: 12px;
}

.wf-role__step {
  padding: 1px 7px;
  border: 1px solid var(--border);
  border-radius: 999px;
  color: var(--text);
  font-size: 11.5px;
}

.wf-ok {
  display: flex;
  align-items: flex-start;
  gap: 6px;
  margin: 16px 0 0;
  color: var(--muted);
  font-size: 12px;
}

.wf-ok svg {
  width: 14px;
  height: 14px;
  flex-shrink: 0;
  margin-top: 1px;
  color: var(--running);
}

.wf-ok--plain {
  margin-top: 4px;
  padding-left: 20px;
}
</style>
