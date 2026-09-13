<script setup lang="ts">
import "@/components/sessions/new-session/new-session.css";
import { computed, nextTick, onMounted, onUnmounted, shallowRef, useTemplateRef, watch } from "vue";
import { useNavigate, useSearch } from "@tanstack/vue-router";
import { ArrowUp, CircleDot, GitPullRequest, LoaderCircle, X } from "lucide-vue-next";
import { storeToRefs } from "pinia";
import { Button } from "@/components/ui/button";
import FolderPicker from "@/components/sessions/new-session/FolderPicker.vue";
import HarnessPicker from "@/components/sessions/new-session/HarnessPicker.vue";
import MoreOptions from "@/components/sessions/new-session/MoreOptions.vue";
import WorkspacePicker from "@/components/sessions/new-session/WorkspacePicker.vue";
import { useEnabledHarnesses } from "@/composables/use-enabled-harnesses";
import { useIsMobile } from "@/composables/use-media-query";
import { useNewSessionDefaults } from "@/composables/use-new-session-defaults";
import { useProjects } from "@/composables/use-projects";
import { useRepositories } from "@/composables/use-repositories";
import { useRepositoryInfo } from "@/composables/use-repository-info";
import { useCreateSession } from "@/composables/use-session-actions";
import { useWorktrees } from "@/composables/use-worktrees";
import { findRepositoryForGitHubPreset, type GitHubSessionSourcePreset } from "@/lib/github-session-source";
import { describeNewSession } from "@/lib/new-session-plan";
import {
  buildCreateSessionRequest,
  resolveNewWorktreeBranch,
  type NewSessionFolder,
  type NewSessionWorkspace,
} from "@/lib/new-session-request";
import { useAppShellStore } from "@/stores/app-shell";
import { useWorkspaceUiStore } from "@/stores/workspace-ui";

const MAX_TEXTAREA_HEIGHT = 180;

const navigate = useNavigate();
const search = useSearch({ from: "/sessions/new" });
const { config } = storeToRefs(useAppShellStore());
const workspaceUiStore = useWorkspaceUiStore();
const { newSessionDialogInitialSource } = storeToRefs(workspaceUiStore);
const { enabledHarnesses, defaultHarnessType } = useEnabledHarnesses();
const defaults = useNewSessionDefaults();
const isMobile = useIsMobile();

const { repositories, scannedAt, error: repositoriesError } = useRepositories();
const { projects } = useProjects();
const { createSession, isLoading: isCreating, error: createError } = useCreateSession();

const message = shallowRef("");
const folder = shallowRef<NewSessionFolder | null>(null);
const workspace = shallowRef<NewSessionWorkspace>({ kind: "new" });
const title = shallowRef("");
const tags = shallowRef("");
const projectId = shallowRef<string | null>(search.value.projectId ?? null);
const harnessType = shallowRef(defaultHarnessType.value);
const gitHubPreset = shallowRef<GitHubSessionSourcePreset | null>(newSessionDialogInitialSource.value);
const validationError = shallowRef<string | null>(null);
/** Set once the folder comes from the person, so defaults arriving later don't replace it. */
const hasChosenFolder = shallowRef(false);
const isFolderMenuOpen = shallowRef(false);

const textareaRef = useTemplateRef<HTMLTextAreaElement>("textarea");

const repositoryPath = computed(() => (folder.value?.kind === "repository" ? folder.value.path : null));
const { worktrees, isLoading: isLoadingWorktrees } = useWorktrees({ repositoryPath });
const { info: repositoryInfo } = useRepositoryInfo(repositoryPath);

const isCloudMode = computed(() => config.value.cloudMode);
const areRepositoriesReady = computed(() => scannedAt.value !== null || repositoriesError.value !== null);
const showHarnessPicker = computed(() => enabledHarnesses.value.length > 1);
const hasMessage = computed(() => message.value.trim().length > 0);
const canSend = computed(() => !isCreating.value && (hasMessage.value || gitHubPreset.value !== null));
const currentBranch = computed(() => repositoryInfo.value?.branch ?? null);

const recentFolders = computed(() => defaults.recentFolders(repositories.value));

const newBranch = computed(() => {
  if (folder.value?.kind !== "repository" || workspace.value.kind !== "new") {
    return undefined;
  }
  return resolveNewWorktreeBranch({ message: message.value, gitHubPreset: gitHubPreset.value });
});

const planParts = computed(() => {
  const selected = workspace.value;
  return describeNewSession({
    folder: folder.value,
    workspace: selected,
    currentBranch: currentBranch.value,
    newBranch: newBranch.value,
    existingBranch: selected.kind === "existing"
      ? worktrees.value.find((worktree) => worktree.path === selected.path)?.branch ?? null
      : null,
  });
});

const placeholder = computed(() => {
  if (gitHubPreset.value) {
    return "Add instructions (optional). The issue is attached.";
  }
  return folder.value?.kind === "none" ? "Ask anything…" : "Describe the task, or ask a question…";
});

const errorMessage = computed(() => validationError.value ?? createError.value ?? null);

const resolvedHarnessType = computed(() => {
  if (enabledHarnesses.value.some((harness) => harness.type === harnessType.value)) {
    return harnessType.value;
  }
  if (enabledHarnesses.value.some((harness) => harness.type === defaultHarnessType.value)) {
    return defaultHarnessType.value;
  }
  return enabledHarnesses.value[0]?.type ?? defaultHarnessType.value;
});

function focusMessage(): void {
  textareaRef.value?.focus({ preventScroll: true });
}

/**
 * When a chip's menu closes after Esc or a choice, focus would fall to <body>: send it back to
 * the message. When it closed because the person clicked something else, leave focus there.
 */
function returnFocusToMessage(event: Event): void {
  event.preventDefault();
  setTimeout(() => {
    const active = document.activeElement;
    if (!active || active === document.body) {
      focusMessage();
    }
  }, 0);
}

function resizeTextarea(): void {
  const textarea = textareaRef.value;
  if (!textarea) {
    return;
  }
  textarea.style.height = "0px";
  textarea.style.height = `${Math.min(textarea.scrollHeight, MAX_TEXTAREA_HEIGHT)}px`;
}

function setFolder(next: NewSessionFolder, chosen: boolean): void {
  folder.value = next;
  hasChosenFolder.value ||= chosen;
  validationError.value = null;
  workspace.value = next.kind === "repository" && !gitHubPreset.value
    ? defaults.workspaceFor(next.path, null)
    : { kind: "new" };
}

function applyInitialFolder(): void {
  if (!areRepositoriesReady.value || hasChosenFolder.value) {
    return;
  }

  if (gitHubPreset.value) {
    const repository = findRepositoryForGitHubPreset(gitHubPreset.value, repositories.value);
    if (repository) {
      setFolder({ kind: "repository", path: repository.path }, false);
    }
    return;
  }

  const initial = defaults.initialFolder(repositories.value);
  if (initial && !(initial.kind === "directory" && isCloudMode.value)) {
    setFolder(initial, false);
  }
}

function removeGitHubPreset(): void {
  gitHubPreset.value = null;
  workspaceUiStore.setNewSessionInitialSource(null);
  focusMessage();
}

async function submit(withoutMessage: boolean): Promise<void> {
  if (isCreating.value) {
    return;
  }

  if (!folder.value) {
    isFolderMenuOpen.value = true;
    return;
  }

  if (!withoutMessage && !hasMessage.value && !gitHubPreset.value) {
    return;
  }

  const request = buildCreateSessionRequest({
    folder: folder.value,
    workspace: workspace.value,
    message: withoutMessage ? "" : message.value,
    title: title.value,
    projectId: projectId.value,
    tags: tags.value.split(","),
    harnessType: resolvedHarnessType.value || undefined,
    gitHubPreset: gitHubPreset.value,
  });

  if (!request.ok) {
    validationError.value = request.error;
    return;
  }

  validationError.value = null;
  const chosenFolder = folder.value;
  const chosenWorkspace = workspace.value;

  try {
    const response = await createSession(request.directory, request.options);
    defaults.remember(chosenFolder, chosenWorkspace);
    workspaceUiStore.setNewSessionInitialSource(null);
    await navigate({
      to: "/sessions/$id",
      params: { id: response.session.id },
      search: {
        instanceId: response.instanceId,
        parentSessionId: undefined,
      },
    });
  } catch {
    // useCreateSession keeps the server's message in createError.
    void nextTick(focusMessage);
  }
}

function handleKeydown(event: KeyboardEvent): void {
  if (event.key !== "Enter" || event.shiftKey || event.isComposing) {
    return;
  }
  // On phones Enter is a new line; the send button submits.
  if (isMobile.value) {
    return;
  }
  event.preventDefault();
  void submit(false);
}

function handleInput(event: Event): void {
  message.value = (event.target as HTMLTextAreaElement).value;
  validationError.value = null;
  resizeTextarea();
}

watch(areRepositoriesReady, applyInitialFolder, { immediate: true });

// A GitHub "start session" can arrive while the page is already open.
watch(newSessionDialogInitialSource, (preset) => {
  if (preset) {
    gitHubPreset.value = preset;
    hasChosenFolder.value = false;
    applyInitialFolder();
  }
});

watch(() => search.value.projectId, (nextProjectId) => {
  projectId.value = nextProjectId ?? null;
  focusMessage();
});

// A remembered worktree that's gone falls back to a new one once the list arrives.
watch(isLoadingWorktrees, (loading, wasLoading) => {
  const selected = workspace.value;
  if (loading || !wasLoading || selected.kind !== "existing") {
    return;
  }
  if (!worktrees.value.some((worktree) => worktree.path === selected.path)) {
    workspace.value = { kind: "new" };
  }
});

watch(
  [enabledHarnesses, defaultHarnessType],
  () => {
    if (!enabledHarnesses.value.some((harness) => harness.type === harnessType.value)) {
      harnessType.value = resolvedHarnessType.value;
    }
  },
  { immediate: true },
);

onMounted(() => {
  focusMessage();
  resizeTextarea();
});

onUnmounted(() => {
  workspaceUiStore.setNewSessionInitialSource(null);
});
</script>

<template>
  <div
    class="new-session"
    data-testid="new-session-form"
  >
    <header class="new-session__header">
      <h2 class="new-session__title">
        New session
      </h2>
      <span class="new-session__pill">Not started</span>
    </header>

    <div class="new-session__stage">
      <div class="new-session__empty">
        <strong>What should we work on?</strong>
        Type below. The chips under the box say where it runs.
      </div>
    </div>

    <section
      class="new-session__composer"
      aria-label="First message"
    >
      <div
        v-if="errorMessage"
        class="new-session__error"
        data-testid="new-session-error"
        role="alert"
      >
        {{ errorMessage }}
      </div>

      <div class="new-session__box">
        <div
          v-if="gitHubPreset"
          class="new-session__attachments"
        >
          <span
            class="new-session__attachment"
            data-testid="new-session-github-attachment"
            :title="gitHubPreset.htmlUrl"
          >
            <GitPullRequest
              v-if="gitHubPreset.sourceType === 'github-pull-request'"
              class="new-session__attachment-icon"
              aria-hidden="true"
            />
            <CircleDot
              v-else
              class="new-session__attachment-icon"
              aria-hidden="true"
            />
            <span class="new-session__attachment-number">#{{ gitHubPreset.number }}</span>
            <span class="new-session__attachment-title">{{ gitHubPreset.title }}</span>
            <button
              type="button"
              class="new-session__attachment-remove"
              :aria-label="`Remove ${gitHubPreset.sourceType === 'github-pull-request' ? 'pull request' : 'issue'} #${gitHubPreset.number}`"
              :disabled="isCreating"
              @click="removeGitHubPreset"
            >
              <X
                class="new-session__attachment-remove-icon"
                aria-hidden="true"
              />
            </button>
          </span>
        </div>

        <textarea
          ref="textarea"
          class="new-session__textarea"
          data-testid="new-session-message"
          aria-label="First message"
          rows="2"
          :value="message"
          :placeholder="placeholder"
          :readonly="isCreating"
          @input="handleInput"
          @keydown="handleKeydown"
        />

        <div class="new-session__toolbar">
          <HarnessPicker
            v-if="showHarnessPicker"
            v-model="harnessType"
            :harnesses="enabledHarnesses"
            :disabled="isCreating"
            @close-auto-focus="returnFocusToMessage"
          />
          <Button
            variant="default"
            size="toolbar-lg"
            class="new-session__send"
            data-testid="create-session-submit"
            aria-label="Start session"
            title="Start session (Enter)"
            :disabled="!canSend"
            @click="submit(false)"
          >
            <LoaderCircle
              v-if="isCreating"
              class="size-4 animate-spin"
            />
            <ArrowUp
              v-else
              class="size-4"
            />
          </Button>
        </div>
      </div>

      <div class="new-session__strip">
        <FolderPicker
          v-model:open="isFolderMenuOpen"
          :folder="folder"
          :repositories="repositories"
          :recent-folders="recentFolders"
          :allow-browse="!isCloudMode && !gitHubPreset"
          :allow-none="!gitHubPreset"
          :disabled="isCreating"
          @update:folder="setFolder($event, true)"
          @close-auto-focus="returnFocusToMessage"
        />
        <template v-if="folder?.kind === 'repository'">
          <span
            class="new-session__strip-separator"
            aria-hidden="true"
          />
          <WorkspacePicker
            :repository-path="folder.path"
            :workspace="workspace"
            :current-branch="currentBranch"
            :worktrees="worktrees"
            :is-loading-worktrees="isLoadingWorktrees"
            :last-worktree-path="defaults.lastWorktreeFor(folder.path)"
            :disabled="isCreating"
            @update:workspace="workspace = $event"
            @close-auto-focus="returnFocusToMessage"
          />
        </template>
        <div class="new-session__strip-end">
          <MoreOptions
            v-model:project-id="projectId"
            v-model:title="title"
            v-model:tags="tags"
            :projects="projects"
            :disabled="isCreating"
            @close-auto-focus="returnFocusToMessage"
          />
        </div>
      </div>

      <p
        class="new-session__plan"
        data-testid="new-session-plan"
        aria-live="polite"
      >
        <template
          v-for="(part, index) in planParts"
          :key="index"
        >
          <code
            v-if="part.code"
            class="new-session__plan-code"
          >{{ part.text }}</code>
          <template v-else>
            {{ part.text }}
          </template>
        </template>
        <button
          v-if="folder && !hasMessage && !gitHubPreset"
          type="button"
          class="new-session__no-message"
          data-testid="create-session-without-message"
          :disabled="isCreating"
          @click="submit(true)"
        >
          Start without a message
        </button>
      </p>
    </section>
  </div>
</template>

<style scoped>
.new-session {
  display: flex;
  height: 100%;
  min-height: 0;
  flex-direction: column;
}

/* Matches the session header: one row, same height and rule. */
.new-session__header {
  display: flex;
  min-height: 56px;
  flex-shrink: 0;
  align-items: center;
  gap: 10px;
  border-bottom: 1px solid var(--border);
  padding: 8px max(1rem, env(safe-area-inset-right)) 8px max(1rem, env(safe-area-inset-left));
}

.new-session__title {
  font-size: 14px;
  font-weight: 600;
  letter-spacing: -0.005em;
  line-height: 1.3;
  color: var(--text);
}

.new-session__pill {
  border: 1px solid var(--border);
  border-radius: 999px;
  padding: 1px 8px;
  color: var(--muted);
  font-size: 11.5px;
  font-weight: 500;
}

.new-session__stage {
  display: flex;
  min-height: 0;
  flex: 1;
  flex-direction: column;
  overflow: auto;
  padding: 20px 24px 8px;
}

.new-session__empty {
  max-width: 560px;
  margin: auto;
  color: var(--muted);
  font-size: 13.5px;
  text-align: center;
}

.new-session__empty strong {
  display: block;
  margin-bottom: 4px;
  color: var(--text);
  font-size: 17px;
  font-weight: 600;
  letter-spacing: -0.01em;
}

/* The composer sits where the session composer does. */
.new-session__composer {
  flex-shrink: 0;
  padding: 4px 24px 18px;
}

.new-session__composer > * {
  max-width: 760px;
  margin-inline: auto;
}

.new-session__error {
  margin-bottom: 10px;
  border: 1px solid color-mix(in srgb, var(--error) 30%, transparent);
  border-radius: var(--radius-card);
  padding: 10px 12px;
  background: color-mix(in srgb, var(--error) 10%, transparent);
  color: var(--error);
  font-size: 12px;
  line-height: 1.5;
}

.new-session__box {
  position: relative;
  border: 1px solid var(--border);
  border-radius: calc(var(--radius-panel) + 2px);
  background: var(--card-bg);
  box-shadow: 0 10px 28px -18px rgba(0, 0, 0, 0.5);
  transition: border-color var(--transition);
}

.new-session__box:focus-within {
  border-color: color-mix(in srgb, var(--accent) 55%, var(--border));
}

.new-session__attachments {
  display: flex;
  flex-wrap: wrap;
  gap: 6px;
  padding: 10px 12px 0;
}

.new-session__attachment {
  display: inline-flex;
  max-width: 100%;
  align-items: center;
  gap: 6px;
  border: 1px solid var(--border);
  border-radius: var(--radius-btn);
  padding: 3px 4px 3px 8px;
  background: color-mix(in srgb, var(--text) 4%, transparent);
  color: var(--text);
  font-size: 12.5px;
}

.new-session__attachment-icon {
  width: 13px;
  height: 13px;
  flex-shrink: 0;
  color: var(--muted);
}

.new-session__attachment-number {
  color: var(--muted);
  font-family: var(--font-mono-stack);
  font-size: 11.5px;
}

.new-session__attachment-title {
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.new-session__attachment-remove {
  display: inline-grid;
  width: 20px;
  height: 20px;
  flex-shrink: 0;
  place-items: center;
  border: 0;
  border-radius: 4px;
  background: transparent;
  color: var(--muted);
  cursor: pointer;
}

.new-session__attachment-remove:hover {
  background: color-mix(in srgb, var(--text) 8%, transparent);
  color: var(--text);
}

.new-session__attachment-remove:focus-visible {
  outline: 2px solid var(--accent);
  outline-offset: 1px;
}

.new-session__attachment-remove-icon {
  width: 12px;
  height: 12px;
}

.new-session__textarea {
  display: block;
  width: 100%;
  min-height: 64px;
  max-height: 180px;
  padding: 14px 16px 6px;
  border: none;
  background: transparent;
  color: var(--text);
  font-size: 14px;
  line-height: 1.5;
  outline: none;
  resize: none;
}

.new-session__textarea::placeholder {
  color: var(--muted);
}

.new-session__toolbar {
  display: flex;
  align-items: center;
  gap: 2px;
  padding: 4px 8px 8px;
}

.new-session__send {
  width: 30px;
  height: 30px;
  margin-left: auto;
  padding: 0;
  border-radius: 999px;
}

.new-session__strip {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 2px;
  padding: 6px 4px 0;
}

.new-session__strip-separator {
  width: 1px;
  height: 14px;
  margin-inline: 3px;
  background: var(--border);
}

.new-session__strip-end {
  display: flex;
  margin-left: auto;
}

.new-session__plan {
  min-height: 22px;
  padding: 4px 11px 0;
  color: var(--muted);
  font-size: 12px;
  line-height: 1.5;
  overflow-wrap: anywhere;
}

.new-session__plan-code {
  color: var(--text);
  font-family: var(--font-mono-stack);
  font-size: 11.5px;
}

.new-session__no-message {
  margin-left: 6px;
  border: 0;
  padding: 0;
  background: none;
  color: var(--muted);
  cursor: pointer;
  font-size: 12px;
  text-decoration: underline;
  text-decoration-color: color-mix(in srgb, var(--muted) 45%, transparent);
  text-underline-offset: 3px;
}

.new-session__no-message:hover:not(:disabled) {
  color: var(--text);
}

.new-session__no-message:focus-visible {
  outline: 2px solid var(--accent);
  outline-offset: 2px;
}

/* Clear the fixed menu button that replaces the sidebar on narrow screens. */
@media (max-width: 716px) {
  .new-session__header {
    padding-left: 44px;
  }
}

@media (max-width: 560px) {
  .new-session__stage,
  .new-session__composer {
    padding-inline: 10px;
  }
}
</style>
