<script setup lang="ts">
import "@/components/sessions/new-session/new-session.css";
import { computed, nextTick, onMounted, shallowRef, useTemplateRef, watch } from "vue";
import { ArrowUp, LoaderCircle } from "lucide-vue-next";
import { storeToRefs } from "pinia";
import { Button } from "@/components/ui/button";
import ComposerFrame from "@/components/session/ComposerFrame.vue";
import BasePicker from "@/components/sessions/new-session/BasePicker.vue";
import FolderPicker from "@/components/sessions/new-session/FolderPicker.vue";
import WorkspacePicker from "@/components/sessions/new-session/WorkspacePicker.vue";
import AutomationMoreOptions from "@/components/automations/AutomationMoreOptions.vue";
import AutomationRunsInPicker from "@/components/automations/AutomationRunsInPicker.vue";
import AutomationWhenPicker from "@/components/automations/AutomationWhenPicker.vue";
import { useIsMobile } from "@/composables/use-media-query";
import { useNewSessionDefaults } from "@/composables/use-new-session-defaults";
import { useRepositories } from "@/composables/use-repositories";
import { useRepositoryDetail } from "@/composables/use-repository-detail";
import type { AutomationComposerState } from "@/composables/use-automations-nav";
import { autoName, parseSchedule, promptFrom, toTrigger, type When } from "@/lib/automation-schedule";
import { browserTimeZone, describeAutomationPlan } from "@/lib/automations";
import type { NewSessionFolder } from "@/lib/new-session-request";
import { useAppShellStore } from "@/stores/app-shell";
import type { Automation, CreateAutomationRequest } from "@/stores/automations";

/**
 * One composer for making and changing an automation, like the new-session page: say what it should do and when,
 * and the chips under the box say where it runs. The schedule is read out of the sentence (no model, no tokens);
 * the When chip shows what was understood and can take over.
 */

const MAX_TEXTAREA_HEIGHT = 190;

const props = defineProps<{
  /** The automation being changed; null while making a new one. */
  automation: Automation | null;
  busy?: boolean;
}>();

/** What's being edited: the new automation's draft, or the open automation's changes. Edited in place. */
const state = defineModel<AutomationComposerState>("state", { required: true });

const emit = defineEmits<{
  submit: [request: CreateAutomationRequest];
}>();

const { config } = storeToRefs(useAppShellStore());
const { repositories, scannedAt, error: repositoriesError, refresh: refreshRepositories } = useRepositories();
const defaults = useNewSessionDefaults();
const isMobile = useIsMobile();
const textareaRef = useTemplateRef<HTMLTextAreaElement>("textarea");
const mirrorRef = useTemplateRef<HTMLDivElement>("mirror");
const isFolderMenuOpen = shallowRef(false);
const fetchOrigin = shallowRef(true);
const branchName = shallowRef("");
const timeZone = browserTimeZone() ?? "UTC";

const isEditing = computed(() => props.automation !== null);
const repositoryPath = computed(() => (state.value.folder?.kind === "repository" ? state.value.folder.path : null));
const { detail: repositoryDetail, isLoading: isLoadingRepositoryDetail } = useRepositoryDetail(repositoryPath);
const currentBranch = computed(() => repositoryDetail.value?.branch ?? null);
const defaultBase = computed(() => repositoryDetail.value?.defaultBase ?? null);
const isWorktree = computed(() => state.value.folder?.kind === "repository" && state.value.workspace.kind === "new");
const recentFolders = computed(() => defaults.recentFolders(repositories.value));
const areRepositoriesReady = computed(() => scannedAt.value !== null || repositoriesError.value !== null);

/** The schedule words in the text; ignored once the When menu has chosen. */
const hit = computed(() => parseSchedule(state.value.text));
const activeHit = computed(() => (state.value.manualWhen ? null : hit.value));

const when = computed<When | null>(() => {
  if (state.value.manualWhen) return state.value.manualWhen;
  const parsed = activeHit.value?.when ?? null;
  if (parsed?.kind === "weekly" && state.value.forceOnce && parsed.days.length === 1) {
    return { kind: "once", day: parsed.days[0], t: parsed.t };
  }
  return parsed;
});

const prompt = computed(() => promptFrom(state.value.text, activeHit.value));

/** The text split around the schedule words, for the highlight under the text area. */
const mirror = computed(() => {
  const text = state.value.text;
  const found = activeHit.value;
  return found
    ? { before: text.slice(0, found.start), marked: text.slice(found.start, found.end), after: text.slice(found.end) }
    : { before: text, marked: "", after: "" };
});
const namePlaceholder = computed(() => autoName(prompt.value));

const plan = computed(() => describeAutomationPlan({
  text: state.value.text,
  when: when.value,
  hit: activeHit.value,
  prompt: prompt.value,
  forceOnce: state.value.forceOnce,
  folder: state.value.folder,
  worktree: isWorktree.value,
  base: isWorktree.value ? state.value.baseBranch ?? defaultBase.value ?? currentBranch.value : null,
  sameSession: state.value.targetType === "same_session",
  timeZone,
  ...(props.automation && props.automation.triggerType !== "event" ? { savedTimeZone: props.automation.timeZone ?? null } : {}),
  legacyFolderless: Boolean(props.automation && !props.automation.isolation && !props.automation.workspaceId),
}));

/** What the server is sent. Model, agent and the per-hour and timeout limits aren't shown, so they're kept. */
const request = computed<CreateAutomationRequest | null>(() => {
  const schedule = when.value;
  if (!schedule || !prompt.value) return null;

  const folder = state.value.folder;
  const workspaceId = folder && folder.kind !== "none" ? folder.path : null;
  const existing = props.automation;
  return {
    name: state.value.name.trim() || existing?.name || autoName(prompt.value),
    prompt: prompt.value,
    ...toTrigger(schedule),
    // Skipping keeps an older automation's own limit; the page only says on or off.
    maxConcurrentRuns: state.value.skip ? Math.max(1, existing?.maxConcurrentRuns ?? 1) : 0,
    maxRunsPerHour: existing?.maxRunsPerHour ?? 10,
    timeoutMinutes: existing?.timeoutMinutes ?? 30,
    workspaceId,
    model: existing?.model ?? null,
    agent: existing?.agent ?? null,
    targetType: state.value.targetType,
    targetTags: existing?.targetTags ? [...existing.targetTags] : [],
    timeZone,
    // An older automation with no folder keeps running in the first workspace root until one is picked.
    isolation: existing && !existing.isolation && !workspaceId ? null : isWorktree.value ? "worktree" : "existing",
    baseBranch: isWorktree.value ? state.value.baseBranch : null,
  };
});

/** Whether saving would change anything about the automation being edited. */
const isDirty = computed(() => {
  const existing = props.automation;
  const next = request.value;
  if (!existing) return true;
  if (!next) return true;
  return next.name !== existing.name
    || next.prompt !== existing.prompt
    || next.triggerType !== existing.triggerType
    || next.triggerConfig !== existing.triggerConfig
    || (next.workspaceId ?? null) !== (existing.workspaceId ?? null)
    // An older automation with a folder ran in it as it is, which is what "existing" means.
    || (next.isolation ?? null) !== (existing.isolation ?? (existing.workspaceId ? "existing" : null))
    || (next.baseBranch ?? null) !== (existing.baseBranch ?? null)
    || next.targetType !== (existing.targetType ?? "new_session")
    || next.maxConcurrentRuns !== existing.maxConcurrentRuns;
});

const canSubmit = computed(() => !props.busy && request.value !== null && isDirty.value);
const showSend = computed(() => !isEditing.value || isDirty.value);

const placeholder = "What should it do, and when? e.g. “Every Monday at 9, summarise the open PRs”";

function focusMessage(): void {
  textareaRef.value?.focus({ preventScroll: true });
}

function returnFocusToMessage(event: Event): void {
  event.preventDefault();
  setTimeout(() => {
    const active = document.activeElement;
    if (!active || active === document.body) focusMessage();
  }, 0);
}

function resizeTextarea(): void {
  const textarea = textareaRef.value;
  if (!textarea) return;
  textarea.style.height = "0px";
  const height = `${Math.min(textarea.scrollHeight, MAX_TEXTAREA_HEIGHT)}px`;
  textarea.style.height = height;
  if (mirrorRef.value) mirrorRef.value.style.height = height;
}

function syncMirrorScroll(): void {
  if (mirrorRef.value && textareaRef.value) mirrorRef.value.scrollTop = textareaRef.value.scrollTop;
}

function handleInput(event: Event): void {
  state.value.text = (event.target as HTMLTextAreaElement).value;
  resizeTextarea();
}

function handleKeydown(event: KeyboardEvent): void {
  if (event.key !== "Enter" || event.shiftKey || event.isComposing || isMobile.value) return;
  event.preventDefault();
  submit();
}

function submit(): void {
  if (!canSubmit.value || !request.value) return;
  emit("submit", request.value);
}

function setWhen(next: When | null): void {
  state.value.manualWhen = next;
  state.value.forceOnce = false;
}

function setFolder(next: NewSessionFolder, chosen: boolean): void {
  const current = state.value.folder;
  const samePlace = current && next.kind === current.kind && (next.kind === "none" || (current.kind !== "none" && next.path === current.path));
  if (!samePlace) state.value.baseBranch = null;
  state.value.folder = next;
  state.value.hasChosenFolder ||= chosen;
  // A new automation's runs get their own worktree unless the person says otherwise.
  if (!samePlace && next.kind === "repository" && !isEditing.value) state.value.workspace = { kind: "new" };
}

function applyInitialFolder(): void {
  if (isEditing.value || !areRepositoriesReady.value || state.value.hasChosenFolder || state.value.folder) return;
  const initial = defaults.initialFolder(repositories.value);
  if (initial && !(initial.kind === "directory" && config.value.cloudMode)) setFolder(initial, false);
}

watch(areRepositoriesReady, applyInitialFolder, { immediate: true });
watch(() => state.value.text, () => void nextTick(resizeTextarea));

onMounted(() => {
  resizeTextarea();
  if (!isEditing.value) focusMessage();
});

defineExpose({ focusMessage });
</script>

<template>
  <section
    class="automation-composer"
    :aria-label="isEditing ? 'Automation' : 'New automation'"
  >
    <ComposerFrame>
      <div class="automation-composer__field">
        <!-- One line on purpose: whitespace between these nodes would shift the highlight off the words. -->
        <!-- eslint-disable-next-line vue/max-attributes-per-line, vue/singleline-html-element-content-newline -->
        <div ref="mirror" class="composer-frame__textarea automation-composer__mirror" aria-hidden="true"><span>{{ mirror.before }}</span><mark v-if="mirror.marked" data-testid="automation-schedule-highlight">{{ mirror.marked }}</mark><span>{{ mirror.after }}</span><br></div>
        <textarea
          ref="textarea"
          class="composer-frame__textarea automation-composer__textarea"
          data-testid="automation-message"
          aria-label="What the automation does, and when"
          rows="2"
          :value="state.text"
          :placeholder="placeholder"
          :readonly="busy"
          @input="handleInput"
          @keydown="handleKeydown"
          @scroll="syncMirrorScroll"
        />
      </div>

      <template #toolbar>
        <Button
          v-if="showSend"
          variant="default"
          size="toolbar-lg"
          class="composer-frame__send"
          :class="{ 'automation-composer__save': isEditing }"
          data-testid="automation-submit"
          :aria-label="isEditing ? 'Save changes' : 'Create automation'"
          :title="isEditing ? 'Save changes (Enter)' : 'Create automation (Enter)'"
          :disabled="!canSubmit"
          @click="submit"
        >
          <LoaderCircle
            v-if="busy"
            class="size-4 animate-spin"
          />
          <template v-else-if="isEditing">
            Save
          </template>
          <ArrowUp
            v-else
            class="size-4"
          />
        </Button>
      </template>
    </ComposerFrame>

    <div class="automation-composer__strip">
      <AutomationWhenPicker
        :when="when"
        :parsed="activeHit !== null"
        :message-has-schedule="hit !== null"
        :time-zone="timeZone"
        :disabled="busy"
        @update:when="setWhen"
        @close-auto-focus="returnFocusToMessage"
      />
      <span
        class="automation-composer__separator"
        aria-hidden="true"
      />
      <FolderPicker
        v-model:open="isFolderMenuOpen"
        :folder="state.folder"
        :repositories="repositories"
        :recent-folders="recentFolders"
        :allow-browse="!config.cloudMode"
        :allow-none="true"
        :disabled="busy"
        @update:folder="setFolder($event, true)"
        @close-auto-focus="returnFocusToMessage"
        @folder-added="refreshRepositories"
      />
      <template v-if="state.folder?.kind === 'repository'">
        <WorkspacePicker
          :repository-path="state.folder.path"
          :workspace="state.workspace"
          :current-branch="currentBranch"
          :worktrees="[]"
          :is-loading-worktrees="false"
          :last-worktree-path="null"
          :disabled="busy"
          @update:workspace="state.workspace = $event"
          @close-auto-focus="returnFocusToMessage"
        />
        <BasePicker
          v-if="state.workspace.kind === 'new'"
          v-model:base-branch="state.baseBranch"
          v-model:fetch-origin="fetchOrigin"
          v-model:branch-name="branchName"
          :branches="repositoryDetail?.branches ?? []"
          :default-base="defaultBase"
          :current-branch="currentBranch"
          :is-loading="isLoadingRepositoryDetail || !repositoryDetail"
          :generated-branch="undefined"
          :hide-branch-name="true"
          :hide-fetch="true"
          :disabled="busy"
          @close-auto-focus="returnFocusToMessage"
        />
      </template>
      <AutomationRunsInPicker
        :target-type="state.targetType"
        :disabled="busy"
        @update:target-type="state.targetType = $event"
        @close-auto-focus="returnFocusToMessage"
      />
      <div class="automation-composer__strip-end">
        <AutomationMoreOptions
          v-model:name="state.name"
          v-model:skip="state.skip"
          :name-placeholder="namePlaceholder"
          :disabled="busy"
          @close-auto-focus="returnFocusToMessage"
        />
      </div>
    </div>

    <p
      class="automation-composer__plan"
      data-testid="automation-plan"
      aria-live="polite"
    >
      <template
        v-for="(part, index) in plan.schedule"
        :key="`s${index}`"
      >
        <b v-if="part.strong">{{ part.text }}</b>
        <span
          v-else-if="part.warn"
          class="automation-composer__warn"
        >{{ part.text }}</span>
        <template v-else>
          {{ part.text }}
        </template>
      </template>
      <template v-if="plan.ask">
        <span
          v-if="plan.ask.question"
          class="automation-composer__warn"
        > {{ plan.ask.question }}</span>
        {{ " " }}<button
          type="button"
          class="automation-composer__linkish"
          data-testid="automation-plan-ask"
          @click="state.forceOnce = plan.ask.action === 'just-once'"
        >
          {{ plan.ask.answer }}
        </button>
      </template>
      {{ " " }}<template
        v-for="(part, index) in plan.rest"
        :key="`r${index}`"
      >
        <code
          v-if="part.code"
          class="automation-composer__code"
        >{{ part.text }}</code>
        <span
          v-else-if="part.warn"
          class="automation-composer__warn"
        >{{ part.text }}</span>
        <template v-else>
          {{ part.text }}
        </template>
      </template>
    </p>
  </section>
</template>

<style scoped>
.automation-composer {
  flex-shrink: 0;
  padding: 4px 24px 18px;
}

.automation-composer > * {
  max-width: 760px;
  margin-inline: auto;
}

.automation-composer__field {
  position: relative;
}

/* The mirror sits under the text area with the same box and font, so the highlight lands under the words. */
.automation-composer__mirror {
  position: absolute;
  inset: 0;
  overflow: hidden;
  color: transparent;
  pointer-events: none;
  white-space: pre-wrap;
  overflow-wrap: break-word;
}

.automation-composer__mirror mark {
  border-radius: 4px;
  background: color-mix(in srgb, var(--accent) 16%, transparent);
  box-shadow: 0 0 0 1px color-mix(in srgb, var(--accent) 45%, transparent);
  color: transparent;
}

.automation-composer__textarea {
  position: relative;
  display: block;
  min-height: 70px;
  white-space: pre-wrap;
  overflow-wrap: break-word;
}

.automation-composer__save {
  width: auto;
  padding: 0 14px;
  font-size: 12.5px;
  font-weight: 500;
}

.automation-composer__strip {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 2px;
  padding: 6px 4px 0;
}

.automation-composer__separator {
  width: 1px;
  height: 14px;
  margin-inline: 3px;
  background: var(--border);
}

.automation-composer__strip-end {
  display: flex;
  margin-left: auto;
}

.automation-composer__plan {
  min-height: 22px;
  padding: 4px 11px 0;
  color: var(--muted);
  font-size: 12px;
  line-height: 1.55;
  overflow-wrap: anywhere;
}

.automation-composer__plan b {
  color: var(--text);
  font-weight: 500;
}

.automation-composer__code {
  color: var(--text);
  font-family: var(--font-mono-stack);
  font-size: 11.5px;
}

.automation-composer__warn {
  color: var(--status-waiting);
}

.automation-composer__linkish {
  border: 0;
  padding: 0;
  background: none;
  color: var(--text);
  font: inherit;
  text-decoration: underline;
  text-decoration-color: color-mix(in srgb, var(--muted) 55%, transparent);
  text-underline-offset: 3px;
  cursor: pointer;
}

.automation-composer__linkish:focus-visible {
  outline: 2px solid var(--accent);
  outline-offset: 2px;
}

@media (max-width: 560px) {
  .automation-composer {
    padding-inline: 10px;
  }
}
</style>
