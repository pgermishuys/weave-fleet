<script setup lang="ts">
import { computed, nextTick, onUnmounted, reactive, shallowRef, useTemplateRef, watch } from "vue";
import { useNavigate } from "@tanstack/vue-router";
import { AlertCircle, Ellipsis, Play, Trash2 } from "lucide-vue-next";
import { DropdownMenuItem } from "reka-ui";
import AutomationComposer from "@/components/automations/AutomationComposer.vue";
import StatusGlyph from "@/components/sessions/StatusGlyph.vue";
import { Button } from "@/components/ui/button";
import { DropdownMenu, DropdownMenuContent, DropdownMenuTrigger } from "@/components/ui/dropdown-menu";
import { Switch } from "@/components/ui/switch";
import {
  AlertDialog,
  AlertDialogAction,
  AlertDialogCancel,
  AlertDialogContent,
  AlertDialogDescription,
  AlertDialogFooter,
  AlertDialogHeader,
  AlertDialogTitle,
} from "@/components/ui/alert-dialog";
import { freshComposerState, useAutomationsNav, type AutomationComposerState } from "@/composables/use-automations-nav";
import { useAutomations } from "@/composables/use-automations";
import { useRepositories } from "@/composables/use-repositories";
import { describeDate, fromTrigger, nextRun } from "@/lib/automation-schedule";
import { describeEventType, describeRunState, describeRunTrigger, eventTypeOf } from "@/lib/automations";
import type { NewSessionFolder } from "@/lib/new-session-request";
import { useAutomationsStore, type Automation, type AutomationRun, type CreateAutomationRequest } from "@/stores/automations";

const navigate = useNavigate();
const { viewMode, activeAutomationId, draft, seedSessionId, setActiveAutomation, resetDraft, clearSelection } = useAutomationsNav();
const {
  automations,
  createAutomation,
  updateAutomation,
  deleteAutomation,
  enableAutomation,
  disableAutomation,
  runAutomation,
  fetchRuns,
  refresh,
} = useAutomations();
const store = useAutomationsStore();
const { repositories } = useRepositories();

const composerRef = useTemplateRef<InstanceType<typeof AutomationComposer>>("composer");
const isSubmitting = shallowRef(false);
const isTogglingEnabled = shallowRef(false);
const deleteConfirmOpen = shallowRef(false);
/** Why the server refused the last create or save; shown above the composer. */
const formError = shallowRef<string | null>(null);
/** A short note: that a run started, that it was saved, or why the switch, Run now or Delete failed. */
const notice = shallowRef<{ kind: "error" | "info"; text: string } | null>(null);
const runs = shallowRef<AutomationRun[]>([]);
const hasLoadedRuns = shallowRef(false);
/** The open automation's changes, reset whenever another one opens or it's saved. */
const editState = reactive<AutomationComposerState>(freshComposerState());
let noticeTimer: ReturnType<typeof setTimeout> | undefined;
let pollTimer: ReturnType<typeof setTimeout> | undefined;

const currentAutomation = computed(() => automations.value.find((a) => a.id === activeAutomationId.value) ?? null);

function messageOf(error: unknown, fallback: string): string {
  return error instanceof Error && error.message ? error.message : fallback;
}

function showNotice(kind: "error" | "info", text: string): void {
  clearTimeout(noticeTimer);
  notice.value = { kind, text };
  if (kind === "info") noticeTimer = setTimeout(() => { notice.value = null; }, 4000);
}

function isRepository(path: string): boolean {
  return repositories.value.some((repository) => repository.path === path);
}

function folderFor(path: string | null, knownRepository: boolean): NewSessionFolder {
  if (!path) return { kind: "none" };
  return knownRepository || isRepository(path) ? { kind: "repository", path } : { kind: "directory", path };
}

/** The composer's state for an automation as it's saved. */
function stateFor(automation: Automation): AutomationComposerState {
  const worktree = automation.isolation === "worktree";
  return {
    ...freshComposerState(),
    text: automation.prompt,
    manualWhen: fromTrigger(automation.triggerType, automation.triggerConfig)
      ?? { kind: "cron", expr: automation.triggerConfig },
    folder: folderFor(automation.workspaceId, worktree),
    hasChosenFolder: true,
    workspace: worktree ? { kind: "new" } : { kind: "current" },
    baseBranch: automation.baseBranch ?? null,
    targetType: automation.targetType ?? "new_session",
    name: automation.name,
    skip: automation.maxConcurrentRuns > 0,
  };
}

function resetEditState(): void {
  const automation = currentAutomation.value;
  Object.assign(editState, automation ? stateFor(automation) : freshComposerState());
}

// A note, an error and the unsaved changes belong to the automation they were about.
watch([activeAutomationId, viewMode], () => {
  formError.value = null;
  notice.value = null;
  runs.value = [];
  hasLoadedRuns.value = false;
  resetEditState();
  void loadRuns();
}, { immediate: true });

// The list can arrive after the page opens.
watch(() => currentAutomation.value?.id, (id, previous) => {
  if (id && !previous) resetEditState();
});

// A folder read before the repository list arrived is a repository after all.
watch(repositories, () => {
  for (const state of [editState, draft]) {
    const folder = state.folder;
    if (folder?.kind === "directory" && isRepository(folder.path)) {
      state.folder = { kind: "repository", path: folder.path };
    }
  }
});

// "Repeat on a schedule…": the session's first message and folder, then the person adds when.
watch(seedSessionId, async (sessionId) => {
  if (!sessionId) return;
  try {
    const seed = await store.fetchDraftFromSession(sessionId);
    Object.assign(draft, freshComposerState(), {
      text: seed.prompt,
      folder: folderFor(seed.folder, seed.isolation === "worktree"),
      hasChosenFolder: true,
      workspace: seed.isolation === "worktree" || !seed.folder ? { kind: "new" } : { kind: "current" },
    });
    composerRef.value?.focusMessage();
  } catch (error) {
    formError.value = messageOf(error, "Couldn't read that session.");
  } finally {
    seedSessionId.value = null;
  }
}, { immediate: true });

async function loadRuns(): Promise<void> {
  clearTimeout(pollTimer);
  const id = activeAutomationId.value;
  if (viewMode.value !== "edit" || !id) return;
  try {
    const next = await fetchRuns(id);
    if (activeAutomationId.value === id) {
      runs.value = next;
      hasLoadedRuns.value = true;
    }
  } catch {
    // The runs list keeps what it had; the next poll tries again.
  }
  // Quickly while a run is starting or going, so its row changes when it finishes; slowly otherwise.
  const busy = runs.value.some((run) => run.state === "starting" || run.state === "running");
  pollTimer = setTimeout(() => {
    void refresh();
    void loadRuns();
  }, busy ? 3000 : 20000);
}

onUnmounted(() => {
  clearTimeout(pollTimer);
  clearTimeout(noticeTimer);
});

async function handleCreate(request: CreateAutomationRequest): Promise<void> {
  formError.value = null;
  isSubmitting.value = true;
  try {
    const created = await createAutomation(request);
    resetDraft();
    setActiveAutomation(created.id);
    // Opening it clears the page's notes, so the note comes after.
    await nextTick();
    const first = created.nextRunAt ? new Date(created.nextRunAt) : null;
    showNotice("info", first ? `Created and on. First run ${describeDate(first)}.` : "Created and on.");
  } catch (error) {
    formError.value = messageOf(error, "Couldn't create the automation.");
  } finally {
    isSubmitting.value = false;
  }
}

async function handleSave(request: CreateAutomationRequest): Promise<void> {
  const automation = currentAutomation.value;
  if (!automation) return;
  formError.value = null;
  isSubmitting.value = true;
  try {
    await updateAutomation(automation.id, request);
    resetEditState();
    showNotice("info", "Saved.");
  } catch (error) {
    formError.value = messageOf(error, "Couldn't save the automation.");
  } finally {
    isSubmitting.value = false;
  }
}

async function handleRunNow(): Promise<void> {
  const automation = currentAutomation.value;
  if (!automation) return;
  try {
    const run = await runAutomation(automation.id);
    runs.value = [run, ...runs.value.filter((existing) => existing.id !== run.id)];
    showNotice("info", "Started a run. It shows below and in Sessions.");
    void loadRuns();
  } catch (error) {
    showNotice("error", messageOf(error, "Couldn't start a run."));
  }
}

async function handleToggleEnabled(enabled: boolean): Promise<void> {
  const automation = currentAutomation.value;
  if (!automation || isTogglingEnabled.value) return;
  isTogglingEnabled.value = true;
  try {
    if (enabled) {
      await enableAutomation(automation.id);
    } else {
      await disableAutomation(automation.id);
    }
  } catch (error) {
    showNotice("error", messageOf(error, enabled ? "Couldn't switch it on." : "Couldn't switch it off."));
  } finally {
    isTogglingEnabled.value = false;
  }
}

async function confirmDelete(): Promise<void> {
  const automation = currentAutomation.value;
  if (!automation) return;
  try {
    await deleteAutomation(automation.id);
    clearSelection();
  } catch (error) {
    showNotice("error", messageOf(error, "Couldn't delete the automation."));
  } finally {
    deleteConfirmOpen.value = false;
  }
}

/** What the header says beside the switch. */
const headerMeta = computed(() => {
  const automation = currentAutomation.value;
  if (!automation) return "";
  if (!automation.isEnabled) {
    if (automation.triggerType === "once" && automation.lastRun) {
      return `Off. Its one run was ${describeDate(new Date(automation.lastRun.startedAt))}.`;
    }
    return "Off. It won't run until you switch it on.";
  }
  if (automation.triggerType === "event") {
    return `Runs when ${describeEventType(eventTypeOf(automation.triggerConfig)).replace(/^A /, "a ").toLowerCase()}`;
  }
  return automation.nextRunAt ? `Next run ${describeDate(new Date(automation.nextRunAt))}` : "";
});

const doneCount = computed(() => runs.value.filter((run) => run.state === "done").length);

/** When a run was for: its schedule time, or when it started. */
function runTime(run: AutomationRun): string {
  return describeDate(new Date(run.scheduledFor ?? run.startedAt));
}

function openRun(run: AutomationRun): void {
  if (!run.sessionId) return;
  void navigate({
    to: "/sessions/$id",
    params: { id: run.sessionId },
    search: { instanceId: run.instanceId ?? undefined, parentSessionId: undefined },
  });
}

/** The empty runs list says when the first one will be. */
const firstRunHint = computed(() => {
  const automation = currentAutomation.value;
  if (!automation?.isEnabled) return "Switch it on, or press Run now.";
  if (automation.nextRunAt) return `The first run is ${describeDate(new Date(automation.nextRunAt))}, or press Run now.`;
  if (automation.triggerType === "event") return "It runs when its event happens, or press Run now.";
  const when = fromTrigger(automation.triggerType, automation.triggerConfig);
  const next = when ? nextRun(when) : null;
  return next ? `The first run is ${describeDate(next)}, or press Run now.` : "Press Run now to try it.";
});
</script>

<template>
  <div class="automation-page">
    <!-- A new automation -->
    <template v-if="viewMode === 'create'">
      <header class="automation-page__header">
        <h2 class="automation-page__title">
          New automation
        </h2>
        <span class="automation-page__pill">Not saved</span>
      </header>
      <div class="automation-page__body">
        <div class="automation-page__empty">
          <strong>What should run on its own?</strong>
          Say what it should do and when. The chips under the box say where it runs.
        </div>
      </div>
      <div
        v-if="formError"
        class="automation-page__error"
        role="alert"
        data-testid="automation-form-error"
      >
        <AlertCircle
          class="size-4 shrink-0"
          aria-hidden="true"
        />
        {{ formError }}
      </div>
      <AutomationComposer
        ref="composer"
        v-model:state="draft"
        :automation="null"
        :busy="isSubmitting"
        @submit="handleCreate"
      />
    </template>

    <!-- An existing automation: its runs, with its composer underneath -->
    <template v-else-if="viewMode === 'edit' && currentAutomation">
      <header class="automation-page__header">
        <h2
          class="automation-page__title"
          data-testid="automation-title"
        >
          {{ currentAutomation.name }}
        </h2>
        <Switch
          :model-value="currentAutomation.isEnabled"
          :disabled="isTogglingEnabled"
          :aria-label="currentAutomation.isEnabled ? 'On' : 'Off'"
          data-testid="automation-enabled"
          @update:model-value="(value: boolean) => handleToggleEnabled(value)"
        />
        <span
          class="automation-page__meta"
          data-testid="automation-header-meta"
        >{{ headerMeta }}</span>
        <span class="automation-page__actions">
          <Button
            variant="outline"
            size="sm"
            data-testid="automation-run-now"
            @click="handleRunNow"
          >
            <Play
              class="size-3.5"
              aria-hidden="true"
            />
            Run now
          </Button>
          <DropdownMenu :modal="false">
            <DropdownMenuTrigger as-child>
              <Button
                variant="outline"
                size="icon-sm"
                aria-label="More"
                data-testid="automation-more-menu"
              >
                <Ellipsis
                  class="size-4"
                  aria-hidden="true"
                />
              </Button>
            </DropdownMenuTrigger>
            <DropdownMenuContent
              class="ns-pop"
              align="end"
              :side-offset="6"
            >
              <DropdownMenuItem
                class="ns-option automation-page__danger"
                data-testid="automation-delete"
                @select="deleteConfirmOpen = true"
              >
                <Trash2
                  class="ns-option__icon"
                  aria-hidden="true"
                />
                <span class="ns-option__text">
                  <span class="ns-option__title">Delete</span>
                </span>
              </DropdownMenuItem>
            </DropdownMenuContent>
          </DropdownMenu>
        </span>
      </header>

      <div class="automation-page__body">
        <div
          v-if="notice"
          :class="['automation-page__notice', `automation-page__notice--${notice.kind}`]"
          :role="notice.kind === 'error' ? 'alert' : 'status'"
          data-testid="automation-action-message"
        >
          <AlertCircle
            v-if="notice.kind === 'error'"
            class="size-4 shrink-0"
            aria-hidden="true"
          />
          {{ notice.text }}
        </div>

        <div
          v-if="hasLoadedRuns && runs.length === 0"
          class="automation-page__empty"
          data-testid="automation-no-runs"
        >
          <strong>No runs yet</strong>
          {{ firstRunHint }}
        </div>
        <section
          v-else-if="runs.length > 0"
          class="automation-runs"
          aria-label="Runs"
        >
          <div class="automation-runs__label">
            <span>Runs</span>
            <span>{{ doneCount }} of {{ runs.length }} finished</span>
          </div>
          <component
            :is="run.sessionId ? 'button' : 'div'"
            v-for="run in runs"
            :key="run.id"
            :type="run.sessionId ? 'button' : undefined"
            class="automation-run"
            :class="{ 'automation-run--openable': run.sessionId }"
            data-testid="automation-run"
            @click="openRun(run)"
          >
            <span class="automation-run__glyph">
              <StatusGlyph
                v-if="run.state === 'running' || run.state === 'starting'"
                status="active"
                :label="describeRunState(run).label"
              />
              <StatusGlyph
                v-else-if="run.state === 'failed'"
                status="error"
                label="Failed"
              />
            </span>
            <span class="automation-run__title">
              {{ runTime(run) }}
              <small v-if="describeRunTrigger(run.trigger)">{{ describeRunTrigger(run.trigger) }}</small>
            </span>
            <span class="automation-run__end">
              <span
                v-if="run.sessionId"
                class="automation-run__open"
              >Open session →</span>
              <span
                class="automation-run__state"
                :class="`automation-run__state--${describeRunState(run).tone}`"
              >{{ describeRunState(run).label }}</span>
            </span>
            <span
              v-if="run.error"
              class="automation-run__why"
            >{{ run.error }}</span>
          </component>
        </section>
      </div>

      <div
        v-if="formError"
        class="automation-page__error"
        role="alert"
        data-testid="automation-form-error"
      >
        <AlertCircle
          class="size-4 shrink-0"
          aria-hidden="true"
        />
        {{ formError }}
      </div>
      <AutomationComposer
        ref="composer"
        v-model:state="editState"
        :automation="currentAutomation"
        :busy="isSubmitting"
        @submit="handleSave"
      />
    </template>

    <div
      v-else
      class="automation-page__body"
    >
      <div class="automation-page__empty">
        <strong>Automations</strong>
        Pick one on the left, or make a new one.
      </div>
    </div>

    <AlertDialog v-model:open="deleteConfirmOpen">
      <AlertDialogContent>
        <AlertDialogHeader>
          <AlertDialogTitle>Delete this automation?</AlertDialogTitle>
          <AlertDialogDescription>
            It stops running. The sessions its runs started stay in Sessions.
          </AlertDialogDescription>
        </AlertDialogHeader>
        <AlertDialogFooter>
          <AlertDialogCancel>Cancel</AlertDialogCancel>
          <AlertDialogAction @click="confirmDelete">
            Delete
          </AlertDialogAction>
        </AlertDialogFooter>
      </AlertDialogContent>
    </AlertDialog>
  </div>
</template>

<style scoped>
.automation-page {
  display: flex;
  height: 100%;
  min-height: 0;
  flex-direction: column;
}

/* Matches the session header: one row, same height and rule. */
.automation-page__header {
  display: flex;
  min-height: 56px;
  flex-shrink: 0;
  align-items: center;
  gap: 10px;
  border-bottom: 1px solid var(--border);
  padding: 8px max(1rem, env(safe-area-inset-right)) 8px max(1rem, env(safe-area-inset-left));
}

.automation-page__title {
  overflow: hidden;
  color: var(--text);
  font-size: 14px;
  font-weight: 600;
  letter-spacing: -0.005em;
  line-height: 1.3;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.automation-page__pill {
  border: 1px solid var(--border);
  border-radius: 999px;
  padding: 1px 8px;
  color: var(--muted);
  font-size: 11.5px;
  font-weight: 500;
}

.automation-page__meta {
  overflow: hidden;
  color: var(--muted);
  font-size: 12px;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.automation-page__actions {
  display: flex;
  flex-shrink: 0;
  align-items: center;
  gap: 6px;
  margin-left: auto;
}

.automation-page__danger {
  color: var(--error);
}

.automation-page__body {
  display: flex;
  min-height: 0;
  flex: 1;
  flex-direction: column;
  overflow-y: auto;
  padding: 18px 24px 8px;
}

.automation-page__empty {
  max-width: 460px;
  margin: auto;
  color: var(--muted);
  font-size: 13.5px;
  text-align: center;
}

.automation-page__empty strong {
  display: block;
  margin-bottom: 4px;
  color: var(--text);
  font-size: 17px;
  font-weight: 600;
  letter-spacing: -0.01em;
}

.automation-page__notice,
.automation-page__error {
  display: flex;
  width: 100%;
  max-width: 760px;
  align-items: flex-start;
  gap: 8px;
  margin: 0 auto 12px;
  border: 1px solid var(--border);
  border-radius: var(--radius-card);
  padding: 9px 12px;
  color: var(--muted);
  font-size: 12.5px;
  line-height: 1.5;
}

.automation-page__notice--error,
.automation-page__error {
  border-color: color-mix(in srgb, var(--error) 30%, transparent);
  background: color-mix(in srgb, var(--error) 10%, transparent);
  color: var(--error);
}

.automation-page__error {
  width: calc(100% - 48px);
  margin-bottom: 8px;
}

.automation-runs {
  width: 100%;
  max-width: 760px;
  margin: 0 auto;
}

.automation-runs__label {
  display: flex;
  align-items: baseline;
  justify-content: space-between;
  padding: 0 10px 6px;
  color: var(--muted);
  font-size: 11px;
  font-weight: 600;
  letter-spacing: 0.05em;
  text-transform: uppercase;
}

.automation-runs__label span:last-child {
  font-size: 12px;
  font-weight: 400;
  letter-spacing: 0;
  text-transform: none;
}

.automation-run {
  display: grid;
  width: 100%;
  min-height: 34px;
  grid-template-columns: 10px minmax(0, 1fr) auto;
  align-items: center;
  column-gap: 9px;
  border: 0;
  border-radius: var(--radius-btn);
  padding: 5px 10px;
  background: transparent;
  color: var(--text);
  font: inherit;
  text-align: left;
}

.automation-run--openable {
  cursor: pointer;
}

.automation-run--openable:hover {
  background: color-mix(in srgb, var(--text) 5%, transparent);
}

.automation-run--openable:focus-visible {
  outline: 2px solid var(--accent);
  outline-offset: -2px;
}

.automation-run__glyph {
  display: grid;
  width: 10px;
  place-items: center;
}

.automation-run__title {
  font-size: 13px;
  font-variant-numeric: tabular-nums;
}

.automation-run__title small {
  margin-left: 6px;
  color: var(--muted);
  font-size: 12px;
}

.automation-run__end {
  display: flex;
  align-items: center;
  gap: 10px;
}

.automation-run__open {
  color: var(--muted);
  font-size: 12px;
  opacity: 0;
  transition: opacity var(--transition);
}

.automation-run:hover .automation-run__open,
.automation-run:focus-visible .automation-run__open {
  opacity: 1;
}

.automation-run__state {
  color: color-mix(in srgb, var(--muted) 85%, transparent);
  font-size: 12px;
  white-space: nowrap;
}

.automation-run__state--working {
  color: var(--running);
}

.automation-run__state--error {
  color: var(--error);
}

.automation-run__state--warn {
  color: var(--status-waiting);
}

.automation-run__why {
  grid-column: 2 / 4;
  color: var(--muted);
  font-size: 12px;
}

/* Clear the fixed menu button that replaces the sidebar on narrow screens. */
@media (max-width: 716px) {
  .automation-page__header {
    padding-left: 44px;
  }

  .automation-page__meta {
    display: none;
  }
}

@media (max-width: 560px) {
  .automation-page__body {
    padding-inline: 10px;
  }
}
</style>
