<script setup lang="ts">
import "@/components/sessions/new-session/new-session.css";
import { computed, nextTick, onMounted, onUnmounted, shallowRef, toRefs, useTemplateRef, watch } from "vue";
import { useNavigate, useSearch } from "@tanstack/vue-router";
import { ArrowUp, CircleDot, GitPullRequest, LoaderCircle, X } from "lucide-vue-next";
import { storeToRefs } from "pinia";
import { Button } from "@/components/ui/button";
import AgentSelector from "@/components/session/AgentSelector.vue";
import ComposerFrame from "@/components/session/ComposerFrame.vue";
import ModelSelector from "@/components/session/ModelSelector.vue";
import MessageBubble from "@/components/session/MessageBubble.vue";
import BasePicker from "@/components/sessions/new-session/BasePicker.vue";
import FolderPicker from "@/components/sessions/new-session/FolderPicker.vue";
import GitHubItemPicker from "@/components/sessions/new-session/GitHubItemPicker.vue";
import HarnessPicker from "@/components/sessions/new-session/HarnessPicker.vue";
import MoreOptions from "@/components/sessions/new-session/MoreOptions.vue";
import ProfilePicker from "@/components/sessions/new-session/ProfilePicker.vue";
import WorkspacePicker from "@/components/sessions/new-session/WorkspacePicker.vue";
import { useEnabledHarnesses } from "@/composables/use-enabled-harnesses";
import { useGitHubPicker } from "@/composables/use-github-picker";
import { useHarnessCatalog } from "@/composables/use-harness-catalog";
import { useSettingsNav } from "@/composables/use-settings-nav";
import { useIsMobile } from "@/composables/use-media-query";
import { useNewSessionDefaults } from "@/composables/use-new-session-defaults";
import { useProjects } from "@/composables/use-projects";
import { useRepositories } from "@/composables/use-repositories";
import { useRepositoryDetail } from "@/composables/use-repository-detail";
import { seedSentPrompt } from "@/composables/use-send-prompt";
import { useCreateSession } from "@/composables/use-session-actions";
import { useWorktrees } from "@/composables/use-worktrees";
import { useWorktreeNamingStore } from "@/stores/worktree-naming";
import { resolveWorktreeName } from "@/lib/worktree-naming";
import { describeDefaults, keepOffered, modelFromKey } from "@/lib/agent-model-choice";
import { findRepositoryForGitHubPreset } from "@/lib/github-session-source";
import { itemResourceId, itemSessionPreset, type GitHubItemSummary } from "@/lib/github-items";
import { findHashTrigger, parseGitHubRemote, removeTrigger } from "@/lib/github-reference";
import { describeNewSession } from "@/lib/new-session-plan";
import {
  buildCreateSessionRequest,
  buildCreatedSessionRow,
  resolveNewWorktreeBranch,
  type NewSessionFolder,
} from "@/lib/new-session-request";
import { NO_PROFILE } from "@/api/client";
import { useAppShellStore } from "@/stores/app-shell";
import { useHarnessProfilesStore } from "@/stores/harness-profiles";
import { useHarnessSetupStore } from "@/stores/harness-setup";
import { useSessionsStore } from "@/stores/sessions";
import { useSmartLinksStore } from "@/stores/smart-links";
import { useWorkspaceUiStore } from "@/stores/workspace-ui";
import { useMachinesStore } from "@/stores/machines";

const MAX_TEXTAREA_HEIGHT = 180;

const navigate = useNavigate();
const search = useSearch({ from: "/sessions/new" });
const { config } = storeToRefs(useAppShellStore());
const workspaceUiStore = useWorkspaceUiStore();
const { newSessionInitialSource } = storeToRefs(workspaceUiStore);
const { enabledHarnesses, defaultHarnessType, noHarnessReason } = useEnabledHarnesses();
const { setActiveSection } = useSettingsNav();
// A new session starts on the machine you're working in.
const machines = useMachinesStore();
const harnessSetup = useHarnessSetupStore();
const defaults = useNewSessionDefaults();
const isMobile = useIsMobile();

const { repositories, scannedAt, error: repositoriesError, refresh: refreshRepositories } = useRepositories();
const { projects } = useProjects();
const { createSession, isLoading: isCreating, error: createError } = useCreateSession();
const sessionsStore = useSessionsStore();

// The page edits a draft kept in the store: it survives leaving the page, and its sidebar row
// follows the message as it's typed.
const { draft, restored } = workspaceUiStore.openNewSessionDraft({
  message: "",
  folder: null,
  hasChosenFolder: false,
  workspace: { kind: "new" },
  baseBranch: null,
  fetchOrigin: true,
  branchName: "",
  title: "",
  tags: "",
  projectId: null,
  harnessType: defaultHarnessType.value,
  harnessProfileId: null,
  agent: "",
  model: "",
  hasChosenAgentOrModel: false,
  gitHubPreset: null,
});
const {
  message,
  folder,
  workspace,
  baseBranch,
  fetchOrigin,
  branchName,
  title,
  tags,
  projectId,
  harnessType,
  harnessProfileId,
  agent,
  model,
  hasChosenAgentOrModel,
  gitHubPreset,
  hasChosenFolder,
} = toRefs(draft);
// A project in the address wins (a project's "+"); otherwise a restored draft keeps its own.
if (search.value.projectId || !restored) {
  projectId.value = search.value.projectId ?? null;
}
// A restored draft's folder was already settled, by the person or by the defaults.
if (restored && folder.value) {
  hasChosenFolder.value = true;
}
// A GitHub "start session" hands its issue over through the store; the draft keeps it from here.
if (newSessionInitialSource.value) {
  gitHubPreset.value = newSessionInitialSource.value;
  hasChosenFolder.value = false;
  workspaceUiStore.setNewSessionInitialSource(null);
}

const validationError = shallowRef<string | null>(null);
const isFolderMenuOpen = shallowRef(false);
/** The message being sent, shown as the conversation's first message while the session starts. */
const sentMessage = computed(() => (draft.isStarting && message.value.trim()) || null);
const sentAt = shallowRef<number | undefined>(undefined);

const textareaRef = useTemplateRef<HTMLTextAreaElement>("textarea");

const repositoryPath = computed(() => (folder.value?.kind === "repository" ? folder.value.path : null));
const { worktrees, isLoading: isLoadingWorktrees } = useWorktrees({ repositoryPath });
const { detail: repositoryDetail, isLoading: isLoadingRepositoryDetail } = useRepositoryDetail(repositoryPath);

const isCloudMode = computed(() => config.value.cloudMode);

// ── # picks a pull request or issue from the folder's repository ─────────────────
const smartLinks = useSmartLinksStore();
const caret = shallowRef<number | null>(null);
// The message when the person closed the picker; it stays shut until they type again.
const dismissedAt = shallowRef<string | null>(null);
const pickerIndex = shallowRef(0);
const hashTrigger = computed(() => (draft.isStarting ? null : findHashTrigger(message.value, caret.value)));
const isPickerOpen = computed(() => hashTrigger.value !== null && dismissedAt.value !== message.value);
const gitHubRepository = computed(() => {
  if (folder.value?.kind !== "repository") return null;
  // origin first; the remote's URL says which GitHub repository it is.
  const remotes = [...(repositoryDetail.value?.remotes ?? [])].sort((a, b) => Number(b.name === "origin") - Number(a.name === "origin"));
  for (const remote of remotes) {
    const repository = remote.github ? { owner: remote.github.owner, repo: remote.github.repo } : parseGitHubRemote(remote.url);
    if (repository) return repository;
  }
  return null;
});
const picker = useGitHubPicker(
  () => (isPickerOpen.value ? gitHubRepository.value : null),
  () => (isPickerOpen.value ? hashTrigger.value?.query ?? null : null),
);
const pickerItems = computed(() => picker.groups.value.flatMap((group) => group.items));
const withSessions = computed(() => {
  const keys = new Set<string>();
  for (const item of pickerItems.value) {
    if (smartLinks.sessionsFor(itemResourceId(item)).length > 0) keys.add(itemResourceId(item).toLowerCase());
  }
  return keys;
});

watch(() => hashTrigger.value?.query, () => {
  pickerIndex.value = 0;
});

function trackCaret(): void {
  caret.value = textareaRef.value?.selectionStart ?? null;
}

/** Starts the session from the picked item, or opens the session already on it when asked. */
function pickGitHubItem(item: GitHubItemSummary, openItsSession = false): void {
  const sessionIds = smartLinks.sessionsFor(itemResourceId(item));
  const existing = sessionsStore.sessions.find((row) => sessionIds.includes(row.session.id));
  if (openItsSession && existing) {
    void navigate({
      to: "/sessions/$id",
      params: { id: existing.session.id },
      search: { instanceId: existing.instanceId, parentSessionId: undefined },
    });
    return;
  }

  const trigger = hashTrigger.value;
  if (trigger) {
    const next = removeTrigger(message.value, trigger);
    message.value = next.text;
    caret.value = next.caret;
    void nextTick(() => {
      const textarea = textareaRef.value;
      if (!textarea) return;
      textarea.value = next.text;
      textarea.setSelectionRange(next.caret, next.caret);
      resizeTextarea();
      focusMessage();
    });
  }
  gitHubPreset.value = itemSessionPreset(item);
  workspace.value = { kind: "new" };
  branchName.value = "";
}

/** Arrow keys, Enter, Tab and Esc drive the picker while it's open. Returns whether the key was its. */
function handlePickerKeydown(event: KeyboardEvent): boolean {
  if (!isPickerOpen.value || event.isComposing) return false;
  const count = pickerItems.value.length;
  switch (event.key) {
    case "ArrowDown":
      event.preventDefault();
      pickerIndex.value = count ? (pickerIndex.value + 1) % count : 0;
      return true;
    case "ArrowUp":
      event.preventDefault();
      pickerIndex.value = count ? (pickerIndex.value - 1 + count) % count : 0;
      return true;
    case "Enter":
    case "Tab": {
      const item = pickerItems.value[Math.min(pickerIndex.value, count - 1)];
      // Still asking GitHub: wait rather than send "Fix #" by accident. With nothing to pick, Enter sends.
      if (!item && picker.isLoading.value) {
        event.preventDefault();
        return true;
      }
      if (!item) return false;
      event.preventDefault();
      pickGitHubItem(item, event.shiftKey);
      return true;
    }
    case "Escape":
      event.preventDefault();
      event.stopPropagation();
      dismissedAt.value = message.value;
      return true;
    default:
      return false;
  }
}
const areRepositoriesReady = computed(() => scannedAt.value !== null || repositoriesError.value !== null);
const showHarnessPicker = computed(() => enabledHarnesses.value.length > 1);
const hasMessage = computed(() => message.value.trim().length > 0);
const isStarting = computed(() => isCreating.value || draft.isStarting);
const canSend = computed(() =>
  !isStarting.value && noHarnessReason.value === null && (hasMessage.value || gitHubPreset.value !== null));
const currentBranch = computed(() => repositoryDetail.value?.branch ?? null);
const defaultBase = computed(() => repositoryDetail.value?.defaultBase ?? null);

/** Where a new worktree starts: the chosen base, else the default once the repository's detail is in. */
const newWorktreeBase = computed(() => {
  if (baseBranch.value) return baseBranch.value;
  if (!repositoryDetail.value) return null;
  return defaultBase.value ?? currentBranch.value;
});

const recentFolders = computed(() => defaults.recentFolders(repositories.value));

const worktreeNaming = useWorktreeNamingStore();

// The templates are read per repository, because a repository can ship its own convention.
watch(
  () => (folder.value?.kind === "repository" ? folder.value.path : null),
  (repositoryPath) => {
    if (repositoryPath && worktreeNaming.loadedFor !== repositoryPath) {
      void worktreeNaming.load(repositoryPath);
    }
  },
  { immediate: true },
);

/**
 * What the templates would call this worktree. A preview only: the request carries a branch just
 * when someone chose one, and the server does the naming so every way in names it the same.
 */
const previewedName = computed(() => {
  if (folder.value?.kind !== "repository" || workspace.value.kind !== "new") {
    return null;
  }

  return resolveWorktreeName(
    worktreeNaming.effective,
    {
      repositoryPath: folder.value.path,
      // {user} and {shortid} are the server's to fill in; a preview that guessed at them would
      // show a name the worktree doesn't get.
      user: "",
      date: new Date().toISOString().slice(0, 10),
      shortId: "",
      home: "",
    },
    message.value,
    resolveNewWorktreeBranch({ branch: branchName.value, gitHubPreset: gitHubPreset.value }),
  );
});

const newBranch = computed(() => previewedName.value?.branch ?? undefined);

/** Where the worktree will land, for the plan line. */
const newWorktreePath = computed(() => {
  const name = previewedName.value;
  return name?.branch && name.folder ? `${name.root}/${name.folder}` : null;
});

/** The name the message (or the GitHub issue) gives the new branch, for the name field's placeholder. */
const generatedBranch = computed(() => previewedName.value?.branch ?? undefined);

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
    defaultBranch: repositoryDetail.value?.defaultBranch ?? null,
    base: newWorktreeBase.value,
    fetchOrigin: fetchOrigin.value,
    worktreePath: newWorktreePath.value,
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

// Profiles: harness config the person keeps in Fleet. The chip shows only when the harness has them and
// there's one to pick; with none, the box looks as it always did.
const harnessProfiles = useHarnessProfilesStore();
const supportsProfiles = computed(() =>
  enabledHarnesses.value.find((harness) => harness.type === resolvedHarnessType.value)?.capabilities?.supportsProfiles === true,
);
const profiles = computed(() => (supportsProfiles.value ? harnessProfiles.profilesFor(resolvedHarnessType.value) : []));
const showProfilePicker = computed(() => profiles.value.length > 0);
/** The picked profile, else the default, else none. A picked profile that's since been deleted falls back too. */
const selectedProfileId = computed<string>({
  get: () => {
    const picked = harnessProfileId.value;
    if (picked === NO_PROFILE || profiles.value.some((profile) => profile.id === picked)) {
      return picked as string;
    }
    return harnessProfiles.defaultFor(resolvedHarnessType.value)?.id ?? NO_PROFILE;
  },
  set: (id) => {
    harnessProfileId.value = id;
  },
});
watch(
  [resolvedHarnessType, supportsProfiles],
  ([type, supported]) => {
    if (supported) void harnessProfiles.load(type);
  },
  { immediate: true },
);

/**
 * The folder the harness is asked about for its agents and models: an existing worktree as it is, otherwise the
 * repository or folder itself (a new worktree is a checkout of it); none for a quick chat. Nothing is asked until
 * the folder is settled.
 */
const catalogHarness = computed(() => (folder.value ? resolvedHarnessType.value : ""));
const catalogDirectory = computed(() => {
  const selected = folder.value;
  if (!selected || selected.kind === "none") {
    return null;
  }
  return selected.kind === "repository" && workspace.value.kind === "existing" ? workspace.value.path : selected.path;
});
/** Asked on the profile the session would start with, which can bring agents and models of its own. */
const catalogProfile = computed(() => (showProfilePicker.value ? selectedProfileId.value : undefined));
const { catalog, agents, models, isSupported: offersAgentsAndModels, isCurrent: isCatalogCurrent } = useHarnessCatalog(
  catalogHarness,
  catalogDirectory,
  catalogProfile,
);

/** Sent unless this folder's catalog says the harness can't take an agent or model. */
const sendsAgentAndModel = computed(() => !(isCatalogCurrent.value && catalog.value?.supported === false));
const defaultLabels = computed(() => describeDefaults(
  offersAgentsAndModels.value ? catalog.value : null,
  { agent: agent.value, model: model.value },
));

const agentChoice = computed({
  get: () => agent.value,
  set: (value: string) => {
    agent.value = value;
    hasChosenAgentOrModel.value = true;
  },
});
const modelChoice = computed({
  get: () => model.value,
  set: (value: string) => {
    model.value = value;
    hasChosenAgentOrModel.value = true;
  },
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
  // A base or branch name belongs to the repository it was picked in.
  if (next.kind !== folder.value?.kind || (next.kind !== "none" && folder.value?.kind !== "none" && next.path !== folder.value?.path)) {
    baseBranch.value = null;
    fetchOrigin.value = true;
    branchName.value = "";
  }
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
  focusMessage();
}

/** Local Fleet sets a harness up in the wizard; in cloud mode it can only be looked at in Settings. */
function fixNoHarness(): void {
  if (!config.value.cloudMode) {
    harnessSetup.open("harnesses");
    return;
  }
  setActiveSection("harnesses");
  void navigate({ to: "/settings" });
}

/** Where the new session's row goes in the sidebar: the chosen project, or Scratch. */
function projectForRow(): { id: string; name: string } | null {
  const chosen = projectId.value
    ? projects.value.find((project) => project.id === projectId.value)
    : projects.value.find((project) => project.type === "scratch");
  return chosen ? { id: chosen.id, name: chosen.name } : null;
}

async function submit(withoutMessage: boolean): Promise<void> {
  if (isStarting.value || noHarnessReason.value !== null) {
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
    harnessProfileId: showProfilePicker.value ? selectedProfileId.value : undefined,
    gitHubPreset: gitHubPreset.value,
    branch: branchName.value,
    baseBranch: baseBranch.value,
    fetchOrigin: fetchOrigin.value,
    agent: sendsAgentAndModel.value ? agent.value : undefined,
    model: sendsAgentAndModel.value ? modelFromKey(model.value) : null,
  });

  if (!request.ok) {
    validationError.value = request.error;
    return;
  }

  validationError.value = null;
  const chosenFolder = folder.value;
  const chosenWorkspace = workspace.value;
  const chosenAgentAndModel = sendsAgentAndModel.value
    ? { harnessType: resolvedHarnessType.value, agent: agent.value, model: model.value }
    : undefined;
  const firstMessage = request.options.initialPrompt ?? null;
  sentAt.value = Date.now();

  // The message moves into the conversation at once; the session catches up.
  draft.isStarting = true;

  try {
    const response = await createSession(request.directory, request.options);
    const sessionId = response.session.id;
    defaults.remember(chosenFolder, chosenWorkspace, chosenAgentAndModel);

    // All in one tick: the session page has the message before its history loads, and the
    // session's row replaces the draft row where it stands.
    if (firstMessage) {
      seedSentPrompt(sessionId, firstMessage, sentAt.value);
    }
    sessionsStore.upsertSession(buildCreatedSessionRow(response, request, projectForRow()));
    workspaceUiStore.handOffNewSessionDraft(sessionId);

    // Someone who left while it started stays where they went; the row is there for them.
    if (!workspaceUiStore.isNewSessionPageOpen) {
      return;
    }
    await navigate({
      to: "/sessions/$id",
      params: { id: sessionId },
      search: {
        instanceId: response.instanceId,
        parentSessionId: undefined,
      },
    });
  } catch {
    // useCreateSession keeps the server's message in createError; the message goes back in the box.
    draft.isStarting = false;
    if (!workspaceUiStore.isNewSessionPageOpen) {
      workspaceUiStore.leaveNewSessionDraft();
      return;
    }
    void nextTick(() => {
      resizeTextarea();
      focusMessage();
    });
  }
}

function handleKeydown(event: KeyboardEvent): void {
  if (handlePickerKeydown(event)) {
    return;
  }
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
  trackCaret();
  resizeTextarea();
}

watch(areRepositoriesReady, applyInitialFolder, { immediate: true });

// A GitHub "start session" can arrive while the page is already open.
watch(newSessionInitialSource, (preset) => {
  if (preset) {
    gitHubPreset.value = preset;
    hasChosenFolder.value = false;
    workspaceUiStore.setNewSessionInitialSource(null);
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

// Each folder starts with the agent and model last used there, until the person picks their own.
watch(
  [folder, resolvedHarnessType],
  ([nextFolder, nextHarness]) => {
    if (!nextFolder || hasChosenAgentOrModel.value) {
      return;
    }
    const remembered = defaults.choiceFor(nextFolder, nextHarness);
    agent.value = remembered.agent;
    model.value = remembered.model;
  },
  { immediate: true },
);

// An agent or model this folder doesn't offer (renamed, or remembered from before) goes back to Default.
watch([catalog, isCatalogCurrent, agent, model], () => {
  if (!isCatalogCurrent.value || !catalog.value?.supported) {
    return;
  }
  const kept = keepOffered({ agent: agent.value, model: model.value }, catalog.value);
  agent.value = kept.agent;
  model.value = kept.model;
}, { immediate: true });

onMounted(() => {
  focusMessage();
  resizeTextarea();
});

onUnmounted(() => {
  workspaceUiStore.leaveNewSessionDraft();
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
      <span
        v-if="machines.hasMachines"
        class="new-session__machine"
        data-testid="new-session-machine"
      >on {{ machines.live.name }}</span>
    </header>

    <!-- Once sent, the message sits where the session page will show it. -->
    <div
      v-if="sentMessage"
      class="new-session__conversation"
      aria-label="Activity stream"
    >
      <div class="new-session__message">
        <MessageBubble
          author="You"
          role="user"
          :body="sentMessage"
          :created-at="sentAt"
          :show-identity="true"
          cluster-position="single"
        />
      </div>
    </div>
    <div
      v-else
      class="new-session__stage"
    >
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

      <!-- Without a ready harness the first message would fail; say why before it's typed. -->
      <div
        v-else-if="noHarnessReason"
        class="new-session__no-harness"
        data-testid="new-session-no-harness"
        role="status"
      >
        <span>{{ noHarnessReason }}</span>
        <button
          type="button"
          class="new-session__no-harness-link"
          @click="fixNoHarness"
        >
          {{ config.cloudMode ? "Open Settings → Harnesses" : "Set up a harness" }}
        </button>
      </div>

      <div class="new-session__frame">
        <GitHubItemPicker
          v-if="isPickerOpen"
          :repository="gitHubRepository ? `${gitHubRepository.owner}/${gitHubRepository.repo}` : null"
          :query="hashTrigger?.query ?? ''"
          :groups="picker.groups.value"
          :selected-index="pickerIndex"
          :is-loading="picker.isLoading.value"
          :error="picker.error.value"
          :with-sessions="withSessions"
          @pick="pickGitHubItem($event)"
          @hover="pickerIndex = $event"
        />

        <ComposerFrame>
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
                :disabled="isStarting"
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
            class="composer-frame__textarea new-session__textarea"
            data-testid="new-session-message"
            aria-label="First message"
            rows="2"
            :value="sentMessage ? '' : message"
            :placeholder="placeholder"
            :readonly="isStarting"
            @input="handleInput"
            @keydown="handleKeydown"
            @keyup="trackCaret"
            @click="trackCaret"
            @blur="dismissedAt = message"
          />

          <template #toolbar>
            <HarnessPicker
              v-if="showHarnessPicker"
              v-model="harnessType"
              :harnesses="enabledHarnesses"
              :disabled="isStarting"
              @close-auto-focus="returnFocusToMessage"
            />
            <ProfilePicker
              v-if="showProfilePicker"
              v-model="selectedProfileId"
              :profiles="profiles"
              :disabled="isStarting"
              @close-auto-focus="returnFocusToMessage"
            />
            <template v-if="offersAgentsAndModels">
              <AgentSelector
                v-model="agentChoice"
                :agents="agents"
                :default-label="defaultLabels.agentLabel"
                :default-description="defaultLabels.agentDescription"
                :disabled="isStarting"
                test-id="new-session-agent"
              />
              <ModelSelector
                v-model="modelChoice"
                :models="models"
                :default-label="defaultLabels.modelLabel"
                :default-description="defaultLabels.modelDescription"
                :disabled="isStarting"
                test-id="new-session-model"
              />
            </template>
            <Button
              variant="default"
              size="toolbar-lg"
              class="composer-frame__send"
              data-testid="create-session-submit"
              aria-label="Start session"
              title="Start session (Enter)"
              :disabled="!canSend"
              @click="submit(false)"
            >
              <LoaderCircle
                v-if="isStarting"
                class="size-4 animate-spin"
              />
              <ArrowUp
                v-else
                class="size-4"
              />
            </Button>
          </template>
        </ComposerFrame>
      </div>

      <div class="new-session__strip">
        <FolderPicker
          v-model:open="isFolderMenuOpen"
          :folder="folder"
          :repositories="repositories"
          :recent-folders="recentFolders"
          :allow-browse="!isCloudMode && !gitHubPreset"
          :allow-none="!gitHubPreset"
          :disabled="isStarting"
          @update:folder="setFolder($event, true)"
          @close-auto-focus="returnFocusToMessage"
          @folder-added="refreshRepositories"
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
            :disabled="isStarting"
            @update:workspace="workspace = $event"
            @close-auto-focus="returnFocusToMessage"
          />
          <BasePicker
            v-if="workspace.kind === 'new'"
            v-model:base-branch="baseBranch"
            v-model:fetch-origin="fetchOrigin"
            v-model:branch-name="branchName"
            :branches="repositoryDetail?.branches ?? []"
            :default-base="defaultBase"
            :current-branch="currentBranch"
            :is-loading="isLoadingRepositoryDetail || !repositoryDetail"
            :generated-branch="generatedBranch"
            :disabled="isStarting"
            @close-auto-focus="returnFocusToMessage"
          />
        </template>
        <div class="new-session__strip-end">
          <MoreOptions
            v-model:project-id="projectId"
            v-model:title="title"
            v-model:tags="tags"
            :projects="projects"
            :disabled="isStarting"
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
          <span
            v-else-if="part.warn"
            class="new-session__plan-warn"
          >{{ part.text }}</span>
          <template v-else>
            {{ part.text }}
          </template>
        </template>
        <button
          v-if="folder && !hasMessage && !gitHubPreset"
          type="button"
          class="new-session__no-message"
          data-testid="create-session-without-message"
          :disabled="isStarting"
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

.new-session__machine {
  padding: 2px 7px;
  border-radius: 5px;
  background: color-mix(in srgb, var(--coral) 14%, transparent);
  color: var(--coral);
  font-family: var(--font-mono);
  font-size: 11px;
  font-weight: 600;
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

/* The sent message sits where the session page's first message will (ActivityStream's layout). */
.new-session__conversation {
  --activity-bubble-width: 100%;
  display: flex;
  min-height: 0;
  flex: 1;
  flex-direction: column;
  overflow-y: auto;
  padding: 24px 32px 12px;
}

.new-session__message {
  display: flex;
  width: 100%;
  max-width: 760px;
  flex-direction: column;
  align-items: flex-end;
  margin: 0 auto 20px;
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

/* The # picker opens above the message box, as wide as it. */
.new-session__frame {
  position: relative;
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

.new-session__no-harness {
  display: flex;
  flex-wrap: wrap;
  align-items: baseline;
  gap: 4px 10px;
  margin-bottom: 10px;
  border: 1px solid color-mix(in srgb, var(--idle) 35%, transparent);
  border-radius: var(--radius-card);
  padding: 10px 12px;
  background: color-mix(in srgb, var(--idle) 10%, transparent);
  color: var(--text);
  font-size: 12px;
  line-height: 1.5;
}

.new-session__no-harness-link {
  border: 0;
  padding: 0;
  background: none;
  color: var(--accent);
  font: inherit;
  font-weight: 500;
  text-decoration: underline;
  text-underline-offset: 2px;
  cursor: pointer;
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

/* Room for two lines before anything is typed. */
.new-session__textarea {
  min-height: 64px;
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

.new-session__plan-warn {
  color: var(--status-waiting);
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
