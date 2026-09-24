<script setup lang="ts">
import "@/components/sessions/new-session/new-session.css";
import { computed, reactive, shallowRef, watch } from "vue";
import { useRouter } from "@tanstack/vue-router";
import { Bot, ChevronDown, GitBranch, LoaderCircle, Play, UserRound } from "lucide-vue-next";
import { storeToRefs } from "pinia";
import { Button } from "@/components/ui/button";
import { Popover, PopoverContent, PopoverTrigger } from "@/components/ui/popover";
import ComposerFrame from "@/components/session/ComposerFrame.vue";
import ModelSelector from "@/components/session/ModelSelector.vue";
import BasePicker from "@/components/sessions/new-session/BasePicker.vue";
import FolderPicker from "@/components/sessions/new-session/FolderPicker.vue";
import HarnessPicker from "@/components/sessions/new-session/HarnessPicker.vue";
import { useBuiltInSkills } from "@/composables/use-built-in-skills";
import { useEnabledHarnesses } from "@/composables/use-enabled-harnesses";
import { useHarnessCatalog } from "@/composables/use-harness-catalog";
import { keyFromPath, pathFromKey, useModelRoles } from "@/composables/use-model-roles";
import { useNewSessionDefaults } from "@/composables/use-new-session-defaults";
import { useRepositories } from "@/composables/use-repositories";
import { useRepositoryDetail } from "@/composables/use-repository-detail";
import { useSettingsNav } from "@/composables/use-settings-nav";
import { useWorkflowsNav } from "@/composables/use-workflows-nav";
import type { NewSessionFolder } from "@/lib/new-session-request";
import { ROLE_NAMES, WORKFLOW_ROLES, skillsInUse, type Workflow, type WorkflowRole } from "@/lib/workflows";
import { useAppShellStore } from "@/stores/app-shell";
import { useWorkflowsStore } from "@/stores/workflows";

/**
 * The Run box: what to build, where (a repository; each run gets its own worktree from the base branch), which
 * optional steps run, and the models this run uses. Like the session composer, with the same chips.
 */
const props = defineProps<{ workflow: Workflow }>();

const router = useRouter();
const store = useWorkflowsStore();
const nav = useWorkflowsNav();
const { config } = storeToRefs(useAppShellStore());
const { repositories, scannedAt, error: repositoriesError, refresh: refreshRepositories } = useRepositories();
const defaults = useNewSessionDefaults();
const { harnesses: allHarnesses, enabledHarnesses, defaultHarnessType } = useEnabledHarnesses();
const { skills: builtInSkills } = useBuiltInSkills();
const { choiceFor } = useModelRoles();
const { setActiveSection } = useSettingsNav();

const request = shallowRef("");
const optionalOn = reactive(new Set<string>());
/** "Check with me after each step": every agent step is one the user finishes, from the first. */
const checkWithMe = shallowRef(false);
const overrides = reactive<Partial<Record<WorkflowRole, string>>>({});
const baseBranch = shallowRef<string | null>(null);
const fetchOrigin = shallowRef(true);
const branchName = shallowRef("");
const harnessChoice = shallowRef<string | null>(null);
const isFolderMenuOpen = shallowRef(false);
const isStarting = shallowRef(false);
const startError = shallowRef<string | null>(null);

const repositoryPath = computed(() => nav.repositoryPath.value);
const { detail: repositoryDetail, isLoading: isLoadingRepositoryDetail } = useRepositoryDetail(repositoryPath);
const recentFolders = computed(() => defaults.recentFolders(repositories.value).filter((folder) => folder.kind === "repository"));
const repositoryOnly = computed(() => repositories.value);
const areRepositoriesReady = computed(() => scannedAt.value !== null || repositoriesError.value !== null);

/** The harnesses a run can use: the ones that can hide the step tool from sessions that aren't steps. */
const workflowHarnesses = computed(() => enabledHarnesses.value.filter((harness) => harness.capabilities.supportsWorkflowSteps));
const harnessType = computed({
  get: () => harnessChoice.value
    ?? (workflowHarnesses.value.some((h) => h.type === defaultHarnessType.value) ? defaultHarnessType.value : workflowHarnesses.value[0]?.type ?? defaultHarnessType.value),
  set: (value: string) => { harnessChoice.value = value; },
});
const harnessName = (type: string) => enabledHarnesses.value.find((h) => h.type === type)?.displayName ?? type;
// Said only once the list has loaded: until then there's nothing to go on.
const notAvailable = computed(() => allHarnesses.value.length > 0 && workflowHarnesses.value.length === 0
  ? `Workflows aren't available on ${harnessName(defaultHarnessType.value)}. Pick OpenCode or OpenCode 2.`
  : null);

const { models, isSupported: offersModels } = useHarnessCatalog(
  computed(() => (repositoryPath.value ? harnessType.value : "")),
  repositoryPath,
);

/** The roles this workflow's enabled steps ask for, in Strong, Standard, Fast order. */
const rolesUsed = computed(() => WORKFLOW_ROLES.filter((role) => props.workflow.steps.some((step) =>
  step.kind === "agent" && step.model === role && (!step.optional || optionalOn.has(step.id)))));

function modelLabel(path: string | null): string {
  if (!path) return "Default model";
  const key = keyFromPath(path);
  return models.value.find((model) => model.selectionKey === key)?.name ?? path.slice(path.indexOf("/") + 1);
}

/** What each role runs on for this run: its override, else Settings → Workflows. */
function roleModel(role: WorkflowRole): string | null {
  const over = overrides[role];
  return over !== undefined ? pathFromKey(over) : choiceFor(harnessType.value, role).model;
}

const overriddenCount = computed(() => WORKFLOW_ROLES.filter((role) => overrides[role] !== undefined).length);

/** A built-in skill an enabled step uses that's off: the run wouldn't start, so say so before Run. */
const skillOff = computed(() => {
  const shipped = new Map(builtInSkills.value.map((skill) => [skill.name, skill.enabled] as const));
  return skillsInUse(props.workflow, optionalOn).find(({ skill }) => shipped.get(skill) === false) ?? null;
});

const cannotRun = computed(() => props.workflow.errors.length > 0
  ? "Fix the workflow file first."
  : notAvailable.value);

const canRun = computed(() => !isStarting.value && !cannotRun.value && !skillOff.value
  && workflowHarnesses.value.length > 0 && request.value.trim().length > 0 && repositoryPath.value !== null);

function toggleOptional(stepId: string): void {
  if (optionalOn.has(stepId)) optionalOn.delete(stepId);
  else optionalOn.add(stepId);
}

function setOverride(role: WorkflowRole, key: string): void {
  // Picking what Settings already says is no override.
  if (pathFromKey(key) === choiceFor(harnessType.value, role).model) delete overrides[role];
  else overrides[role] = key;
}

function setFolder(next: NewSessionFolder, chosen: boolean): void {
  if (next.kind !== "repository") return;
  if (next.path !== repositoryPath.value) baseBranch.value = null;
  nav.setFolder(next, chosen);
}

function applyInitialFolder(): void {
  if (!areRepositoriesReady.value || nav.hasChosenFolder.value || nav.folder.value) return;
  const initial = defaults.initialFolder(repositories.value);
  if (initial?.kind === "repository") setFolder(initial, false);
}

watch(areRepositoriesReady, applyInitialFolder, { immediate: true });
watch(() => props.workflow.id, () => {
  optionalOn.clear();
  for (const role of WORKFLOW_ROLES) delete overrides[role];
  startError.value = null;
});

function openSkillsSettings(): void {
  setActiveSection("skills");
  void router.navigate({ to: "/settings" });
}

async function run(): Promise<void> {
  if (!repositoryPath.value) {
    isFolderMenuOpen.value = true;
    return;
  }
  if (!canRun.value) return;

  isStarting.value = true;
  startError.value = null;
  try {
    const roleOverrides = Object.fromEntries(WORKFLOW_ROLES
      .filter((role) => overrides[role] !== undefined)
      .map((role) => [role, { model: pathFromKey(overrides[role]!), effort: null }]));
    const started = await store.start({
      workflowId: props.workflow.id,
      directory: repositoryPath.value,
      request: request.value.trim(),
      baseBranch: baseBranch.value,
      harnessType: harnessType.value,
      optionalSteps: [...optionalOn],
      roleOverrides,
      checkWithMe: checkWithMe.value,
    });
    request.value = "";
    const first = started.sessions[0]?.sessionId;
    if (first) void router.navigate({ to: "/sessions/$id", params: { id: first }, search: { instanceId: undefined, parentSessionId: undefined } });
  } catch (error) {
    startError.value = error instanceof Error ? error.message : "Couldn't start the run.";
  } finally {
    isStarting.value = false;
  }
}

/** What "Repeat on a schedule…" carries over to the new automation: what's typed and chosen here. */
function scheduleDraft(): { request: string; optionalSteps: string[]; baseBranch: string | null; harnessType: string | null } {
  return {
    request: request.value.trim(),
    optionalSteps: [...optionalOn],
    baseBranch: baseBranch.value,
    harnessType: harnessChoice.value,
  };
}

defineExpose({ scheduleDraft });

function handleKeydown(event: KeyboardEvent): void {
  if (event.key !== "Enter" || event.shiftKey || event.isComposing) return;
  event.preventDefault();
  void run();
}

</script>

<template>
  <div class="wf-run">
    <ComposerFrame>
      <textarea
        v-model="request"
        class="composer-frame__textarea wf-run__textarea"
        data-testid="workflow-request"
        aria-label="What the run should do"
        rows="2"
        :placeholder="workflow.placeholder ?? 'What should it do?'"
        :readonly="isStarting"
        @keydown="handleKeydown"
      />
      <template #toolbar>
        <Button
          variant="default"
          size="toolbar-lg"
          class="composer-frame__send wf-run__go"
          data-testid="workflow-run"
          :disabled="!canRun"
          @click="run"
        >
          <LoaderCircle
            v-if="isStarting"
            class="size-4 animate-spin"
          />
          <Play
            v-else
            class="size-3.5"
          />
          Run
        </Button>
      </template>
    </ComposerFrame>

    <div class="wf-run__strip">
      <FolderPicker
        v-model:open="isFolderMenuOpen"
        :folder="nav.folder.value"
        :repositories="repositoryOnly"
        :recent-folders="recentFolders"
        :allow-browse="!config.cloudMode"
        :allow-none="false"
        :disabled="isStarting"
        @update:folder="setFolder($event, true)"
        @folder-added="refreshRepositories"
      />
      <span
        class="ns-chip wf-run__static"
        title="Each run gets its own worktree and branch; every step works in it."
      >
        <GitBranch
          class="ns-chip__icon"
          aria-hidden="true"
        />
        <span class="ns-chip__label">New worktree</span>
      </span>
      <BasePicker
        v-if="repositoryPath"
        v-model:base-branch="baseBranch"
        v-model:fetch-origin="fetchOrigin"
        v-model:branch-name="branchName"
        :branches="repositoryDetail?.branches ?? []"
        :default-base="repositoryDetail?.defaultBase ?? null"
        :current-branch="repositoryDetail?.branch ?? null"
        :is-loading="isLoadingRepositoryDetail || !repositoryDetail"
        :generated-branch="undefined"
        :hide-branch-name="true"
        :hide-fetch="true"
        :disabled="isStarting"
      />
      <button
        v-for="step in workflow.steps.filter((s) => s.optional)"
        :key="step.id"
        type="button"
        class="ns-chip"
        :aria-pressed="optionalOn.has(step.id)"
        :title="step.optionalHint ?? undefined"
        :data-testid="`workflow-optional-${step.id}`"
        :disabled="isStarting"
        @click="toggleOptional(step.id)"
      >
        <span class="ns-chip__hint">{{ step.title }}:</span>
        <span class="ns-chip__label">{{ optionalOn.has(step.id) ? "on" : "off" }}</span>
      </button>
      <button
        type="button"
        class="ns-chip"
        role="switch"
        :aria-checked="checkWithMe"
        title="Check with me after each step: every step waits for you to move it on, and you can talk to the agent in between. You can change it later from the run's header."
        data-testid="workflow-check-with-me-start"
        :disabled="isStarting"
        @click="checkWithMe = !checkWithMe"
      >
        <UserRound
          class="ns-chip__icon"
          aria-hidden="true"
        />
        <span class="ns-chip__hint">Check with me:</span>
        <span class="ns-chip__label">{{ checkWithMe ? "on" : "off" }}</span>
      </button>
      <HarnessPicker
        v-if="workflowHarnesses.length > 1"
        v-model="harnessType"
        :harnesses="workflowHarnesses"
        :disabled="isStarting"
      />
      <Popover>
        <PopoverTrigger as-child>
          <button
            type="button"
            class="ns-chip"
            data-testid="workflow-models"
            :disabled="isStarting || rolesUsed.length === 0"
          >
            <Bot
              class="ns-chip__icon"
              aria-hidden="true"
            />
            <span class="ns-chip__label">Models<template v-if="overriddenCount"> · {{ overriddenCount }} changed</template></span>
            <ChevronDown
              class="ns-chip__chevron"
              aria-hidden="true"
            />
          </button>
        </PopoverTrigger>
        <PopoverContent
          class="ns-pop wf-models"
          align="start"
        >
          <div class="ns-pop__label">
            Models for this run
          </div>
          <div
            v-for="role in rolesUsed"
            :key="role"
            class="wf-models__row"
          >
            <span class="wf-models__role">{{ ROLE_NAMES[role] }}</span>
            <ModelSelector
              v-if="offersModels"
              :model-value="overrides[role] ?? keyFromPath(choiceFor(harnessType, role).model)"
              :models="models"
              default-label="Default model"
              default-description="The composer's default model"
              :test-id="`workflow-model-${role}`"
              @update:model-value="setOverride(role, $event)"
            />
            <span
              v-else
              class="wf-models__fixed"
            >{{ modelLabel(roleModel(role)) }}</span>
          </div>
          <p class="wf-models__note">
            Only for this run. The roles themselves are set in Settings → Workflows.
          </p>
        </PopoverContent>
      </Popover>
    </div>

    <p
      v-if="cannotRun"
      class="wf-run__warn"
      role="alert"
    >
      {{ cannotRun }}
    </p>
    <p
      v-else-if="skillOff"
      class="wf-run__warn"
      role="alert"
      data-testid="workflow-skill-off"
    >
      {{ skillOff.step.title }} uses {{ skillOff.skill }}, which is off.
      <button
        type="button"
        class="wf-run__link"
        @click="openSkillsSettings"
      >
        Turn it on in Settings → Skills
      </button>
    </p>
    <p
      v-else-if="startError"
      class="wf-run__warn"
      role="alert"
      data-testid="workflow-start-error"
    >
      {{ startError }}
    </p>
    <p
      v-else
      class="wf-run__foot"
    >
      Runs as a group in Sessions. It stops at each <b>You decide</b> step and shows it under Needs you.
    </p>
  </div>
</template>

<style scoped>
.wf-run {
  display: flex;
  flex-direction: column;
  gap: 6px;
}

/* The frame centres itself with auto margins, which in a flex column would also shrink it. */
.wf-run :deep(.composer-frame) {
  width: 100%;
}

.wf-run__textarea {
  min-height: 52px;
}

/* A labelled Run button, not the round send arrow the frame sizes for. */
.wf-run .wf-run__go {
  width: auto;
  gap: 6px;
  padding-inline: 12px;
}

.wf-run__strip {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 2px;
}

.wf-run__static {
  cursor: default;
}

.wf-run__static:hover {
  background: transparent;
}

.wf-run__foot,
.wf-run__warn {
  margin: 0;
  padding: 0 8px;
  font-size: 12px;
  color: var(--muted);
}

.wf-run__foot b {
  color: var(--idle);
  font-weight: 500;
}

.wf-run__warn {
  color: var(--error);
}

.wf-run__link {
  border: 0;
  padding: 0;
  background: none;
  color: var(--accent);
  cursor: pointer;
  font: inherit;
  text-decoration: underline;
}

:global(.wf-models) {
  width: 360px;
}

.wf-models__row {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 10px;
  padding: 4px 8px;
}

.wf-models__role {
  font-size: 12.5px;
  font-weight: 500;
}

.wf-models__fixed {
  font-size: 12.5px;
  color: var(--muted);
}

.wf-models__note {
  margin: 6px 8px 4px;
  font-size: 11.5px;
  color: var(--muted);
}
</style>
