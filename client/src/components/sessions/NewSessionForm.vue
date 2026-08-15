<script setup lang="ts">
import { computed, nextTick, onUnmounted, shallowRef, watch } from "vue";
import { useNavigate, useSearch } from "@tanstack/vue-router";
import { AlertCircle, Check, ChevronDown, ExternalLink, Folder, FolderGit2, LoaderCircle, X } from "lucide-vue-next";
import { storeToRefs } from "pinia";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import DirectoryPickerPopover from "@/components/ui/DirectoryPickerPopover.vue";
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select";
import {
  Collapsible,
  CollapsibleContent,
  CollapsibleTrigger,
} from "@/components/ui/collapsible";
import { useDirectoryBrowser } from "@/composables/use-directory-browser";
import { useEnabledHarnesses } from "@/composables/use-enabled-harnesses";
import { useProjects } from "@/composables/use-projects";
import { useRepositories } from "@/composables/use-repositories";
import { useCreateSession } from "@/composables/use-session-actions";
import { useWorktrees } from "@/composables/use-worktrees";
import type {
  ProjectResponse,
  ScannedRepository,
  SessionSourceSelection,
} from "@/api/client";
import {
  buildGitHubSessionSourceSelection,
  findRepositoryForGitHubPreset,
  type GitHubSessionSourcePreset,
} from "@/lib/github-session-source";
import { cn } from "@/lib/utils";
import { useAppShellStore } from "@/stores/app-shell";
import { useWorkspaceUiStore } from "@/stores/workspace-ui";

type SessionSourceKind = "repository" | "directory";
type IsolationStrategy = "existing" | "worktree";
type WorktreeMode = "new" | "existing";
type WhereToRunMode = "new-worktree" | "existing-worktree" | "repository" | "directory";

const UNGROUPED_PROJECT_ID = "__ungrouped__";

const navigate = useNavigate();
const search = useSearch({ from: "/sessions/new" });
const appShellStore = useAppShellStore();
const workspaceUiStore = useWorkspaceUiStore();
const { config } = storeToRefs(appShellStore);
const { newSessionDialogInitialSource } = storeToRefs(workspaceUiStore);
const {
  enabledHarnesses,
  defaultHarnessType,
} = useEnabledHarnesses();

const whereToRunMode = shallowRef<WhereToRunMode>("new-worktree");
const repositoryQuery = shallowRef("");
const selectedRepositoryPath = shallowRef<string | null>(null);
const isRepositoryListOpen = shallowRef(false);
const highlightedRepoIndex = shallowRef(0);
const isDirectoryPickerOpen = shallowRef(false);
const directory = shallowRef("");
const title = shallowRef("");
const branch = shallowRef("");
const branchManuallyEdited = shallowRef(false);
const selectedWorktreePath = shallowRef<string | null>(null);
const selectedProjectId = shallowRef(search.value.projectId ?? UNGROUPED_PROJECT_ID);
const selectedHarnessType = shallowRef(defaultHarnessType.value);
const submitAttempted = shallowRef(false);
const activeGitHubPreset = shallowRef<GitHubSessionSourcePreset | null>(null);
const tags = shallowRef<string[]>([]);
const isMoreOptionsOpen = shallowRef(false);

const {
  repositories,
  isLoading: isRepositoriesLoading,
  error: repositoriesError,
} = useRepositories();
const {
  projects,
  error: projectsError,
} = useProjects({ enabled: computed(() => true) });
const {
  createSession,
  isLoading: isCreating,
  error: createError,
} = useCreateSession();
const directoryBrowser = useDirectoryBrowser();
const {
  worktrees,
  isLoading: isWorktreesLoading,
} = useWorktrees({ repositoryPath: selectedRepositoryPath, enabled: computed(() => true) });

const userProjects = computed<readonly ProjectResponse[]>(() => {
  return projects.value.filter((project) => project.type !== "scratch");
});

const showHarnessSelect = computed(() => enabledHarnesses.value.length > 1);
const showProjectSelect = computed(() => userProjects.value.length > 0);
const isCloudMode = computed(() => config.value.cloudMode);

const sortedRepositories = computed<readonly ScannedRepository[]>(() => {
  return [...repositories.value].sort((left, right) => left.name.localeCompare(right.name));
});

const filteredRepositories = computed<readonly ScannedRepository[]>(() => {
  const query = repositoryQuery.value.trim().toLowerCase();
  if (!query) {
    return sortedRepositories.value;
  }

  return sortedRepositories.value.filter((repository) => {
    const searchableText = `${repository.name} ${repository.path}`.toLowerCase();
    return searchableText.includes(query);
  });
});

const selectedRepository = computed<ScannedRepository | null>(() => {
  if (!selectedRepositoryPath.value) {
    return null;
  }

  return repositories.value.find((repository) => repository.path === selectedRepositoryPath.value) ?? null;
});

function generateRandomBranch(): string {
  const chars = "abcdefghijklmnopqrstuvwxyz0123456789";
  let result = "";
  for (let i = 0; i < 4; i++) {
    result += chars.charAt(Math.floor(Math.random() * chars.length));
  }
  return `session/wt-${result}`;
}

const generatedBranch = computed(() => {
  if (title.value.trim()) {
    return title.value
      .toLowerCase()
      .trim()
      .replace(/[^a-z0-9\s-]/g, "")
      .replace(/\s+/g, "-")
      .replace(/-+/g, "-")
      .replace(/^-|-$/g, "");
  }
  return generateRandomBranch();
});

const effectiveBranch = computed(() => {
  if (whereToRunMode.value !== "new-worktree") {
    return "";
  }

  return branchManuallyEdited.value ? branch.value.trim() : generatedBranch.value;
});

const resolvedHarnessType = computed(() => {
  if (selectedHarnessType.value && enabledHarnesses.value.some((harness) => harness.type === selectedHarnessType.value)) {
    return selectedHarnessType.value;
  }

  return getPreferredHarnessType();
});

const sourceKind = computed<SessionSourceKind>(() => {
  return whereToRunMode.value === "directory" ? "directory" : "repository";
});

const isolationStrategy = computed<IsolationStrategy>(() => {
  if (whereToRunMode.value === "new-worktree" || whereToRunMode.value === "existing-worktree") {
    return "worktree";
  }
  return "existing";
});

const worktreeMode = computed<WorktreeMode>(() => {
  return whereToRunMode.value === "existing-worktree" ? "existing" : "new";
});

const effectiveDirectory = computed(() => {
  if (sourceKind.value === "repository") {
    return selectedRepository.value?.path ?? "";
  }

  return directory.value.trim();
});

const sessionSource = computed<SessionSourceSelection | undefined>(() => {
  if (sourceKind.value === "repository") {
    if (!selectedRepository.value) {
      return undefined;
    }

    if (activeGitHubPreset.value) {
      return buildGitHubSessionSourceSelection(
        activeGitHubPreset.value,
        selectedRepository.value.path,
        isolationStrategy.value,
        isolationStrategy.value === "existing" ? undefined : effectiveBranch.value || undefined,
      );
    }

    return {
      key: {
        providerId: "builtin.repository",
        sourceType: "repository",
        actionId: "start-session",
        contractVersion: 1,
      },
      input: {
        repositoryPath: selectedRepository.value.path,
        isolationStrategy: isolationStrategy.value,
        ...(isolationStrategy.value === "worktree" && worktreeMode.value === "existing" && selectedWorktreePath.value
          ? { existingWorktreePath: selectedWorktreePath.value }
          : isolationStrategy.value === "worktree" && effectiveBranch.value
            ? { branch: effectiveBranch.value }
            : {}),
      },
    };
  }

  if (!directory.value.trim()) {
    return undefined;
  }

  return {
    key: {
      providerId: "builtin.local",
      sourceType: "directory",
      actionId: "start-session",
      contractVersion: 1,
    },
    input: {
      directory: directory.value.trim(),
      isolationStrategy: "existing",
    },
  };
});

const validationMessage = computed(() => {
  if (sourceKind.value === "repository") {
    if (isRepositoriesLoading.value) {
      return "Loading repositories…";
    }

    if (!selectedRepository.value) {
      return repositories.value.length === 0 ? "No repositories are available." : "Select a repository.";
    }
  }

  if (!isCloudMode.value && sourceKind.value === "directory" && !directory.value.trim()) {
    return "Directory is required.";
  }

  if (sourceKind.value === "repository"
    && isolationStrategy.value === "worktree"
    && worktreeMode.value === "existing"
    && !selectedWorktreePath.value) {
    return "Select a worktree.";
  }

  return null;
});

const formError = computed(() => {
  if (submitAttempted.value && validationMessage.value) {
    return validationMessage.value;
  }

  return createError.value
    ?? repositoriesError.value
    ?? projectsError.value
    ?? directoryBrowser.error.value
    ?? null;
});

const canSubmit = computed(() => {
  return !isCreating.value && sessionSource.value !== undefined && validationMessage.value === null;
});

const gitHubContextPreview = computed(() => {
  const body = activeGitHubPreset.value?.body?.trim();
  if (!body) {
    return "";
  }

  const compactBody = body.replace(/\s+/g, " ").trim();
  const maxLength = 280;
  if (compactBody.length <= maxLength) {
    return compactBody;
  }

  const truncatedBody = compactBody.slice(0, maxLength);
  const lastWhitespaceIndex = truncatedBody.lastIndexOf(" ");
  if (lastWhitespaceIndex < Math.floor(maxLength * 0.6)) {
    return `${truncatedBody.trimEnd()}…`;
  }

  return `${truncatedBody.slice(0, lastWhitespaceIndex).trimEnd()}…`;
});

function getPreferredHarnessType(): string {
  if (enabledHarnesses.value.some((harness) => harness.type === defaultHarnessType.value)) {
    return defaultHarnessType.value;
  }

  return enabledHarnesses.value[0]?.type ?? defaultHarnessType.value;
}

function applyInitialSource(): void {
  activeGitHubPreset.value = newSessionDialogInitialSource.value;

  if (!newSessionDialogInitialSource.value) {
    return;
  }

  whereToRunMode.value = "new-worktree";
  title.value = newSessionDialogInitialSource.value.title;
  branch.value = newSessionDialogInitialSource.value.suggestedBranch?.trim() ?? "";
  branchManuallyEdited.value = Boolean(newSessionDialogInitialSource.value.suggestedBranch?.trim());
  selectedWorktreePath.value = null;
}

function clearGitHubPreset(): void {
  activeGitHubPreset.value = null;
  workspaceUiStore.setNewSessionInitialSource(null);
}

function selectRepository(repository: ScannedRepository): void {
  selectedRepositoryPath.value = repository.path;
  repositoryQuery.value = repository.path;
  isRepositoryListOpen.value = false;
}

function handleRepositoryKeydown(event: KeyboardEvent): void {
  if (!isRepositoryListOpen.value || filteredRepositories.value.length === 0) {
    return;
  }

  if (event.key === "ArrowDown") {
    event.preventDefault();
    highlightedRepoIndex.value = Math.min(highlightedRepoIndex.value + 1, filteredRepositories.value.length - 1);
    scrollHighlightedIntoView();
  } else if (event.key === "ArrowUp") {
    event.preventDefault();
    highlightedRepoIndex.value = Math.max(highlightedRepoIndex.value - 1, 0);
    scrollHighlightedIntoView();
  } else if (event.key === "Tab") {
    const repo = filteredRepositories.value[highlightedRepoIndex.value];
    if (repo) {
      selectRepository(repo);
    }
    isRepositoryListOpen.value = false;
  } else if (event.key === "Enter") {
    event.preventDefault();
    const repo = filteredRepositories.value[highlightedRepoIndex.value];
    if (repo) {
      selectRepository(repo);
    }
  }
}

function scrollHighlightedIntoView(): void {
  nextTick(() => {
    const el = document.querySelector('[data-repo-highlighted="true"]');
    el?.scrollIntoView({ block: "nearest" });
  });
}

function handleRepositoryBlur(): void {
  window.setTimeout(() => {
    isRepositoryListOpen.value = false;

    if (selectedRepository.value && repositoryQuery.value.trim() === selectedRepository.value.path) {
      return;
    }

    if (filteredRepositories.value.length === 1) {
      selectRepository(filteredRepositories.value[0]);
      return;
    }

    selectedRepositoryPath.value = null;
  }, 120);
}

function getDirectoryBrowserStartPath(): string | null {
  const selectedDirectory = directory.value.trim();
  if (selectedDirectory) {
    return selectedDirectory;
  }

  return null;
}

function syncDirectoryBrowser(): void {
  const nextPath = getDirectoryBrowserStartPath();
  directoryBrowser.browse(nextPath);
}

function handleDirectoryPickerOpenChange(value: boolean): void {
  if (value) {
    syncDirectoryBrowser();
  }
  isDirectoryPickerOpen.value = value;
}

function handleDirectorySelected(path: string): void {
  directory.value = path;
  isDirectoryPickerOpen.value = false;
}

function handleCancel(): void {
  void navigate({
    to: "/",
    search: undefined,
  });
}

function handleKeydown(event: KeyboardEvent): void {
  if (event.key === "Escape") {
    event.preventDefault();
    handleCancel();
  } else if ((event.metaKey || event.ctrlKey) && event.key === "Enter") {
    event.preventDefault();
    void handleSubmit();
  }
}

async function handleSubmit(): Promise<void> {
  submitAttempted.value = true;

  if (!canSubmit.value || !sessionSource.value) {
    return;
  }

  try {
    const response = await createSession(effectiveDirectory.value || undefined, {
      title: title.value.trim() || undefined,
      source: sessionSource.value,
      isolationStrategy: isolationStrategy.value,
      branch: isolationStrategy.value === "worktree" ? effectiveBranch.value || undefined : undefined,
      harnessType: resolvedHarnessType.value || undefined,
      projectId: selectedProjectId.value !== UNGROUPED_PROJECT_ID ? selectedProjectId.value : undefined,
      tags: tags.value.length > 0 ? tags.value : undefined,
    });

    await navigate({
      to: "/sessions/$id",
      params: { id: response.session.id },
      search: {
        instanceId: response.instanceId,
        parentSessionId: undefined,
      },
    });
  } catch {
    // Mutation state drives the error banner.
  }
}

watch(
  [repositories, activeGitHubPreset],
  ([nextRepositories, nextGitHubPreset]) => {
    if (selectedRepositoryPath.value || nextRepositories.length === 0) {
      return;
    }

    if (nextGitHubPreset) {
      const matchingRepository = findRepositoryForGitHubPreset(nextGitHubPreset, nextRepositories);
      if (matchingRepository) {
        selectRepository(matchingRepository);
        return;
      }
    }
  },
  { immediate: true },
);

watch(repositoryQuery, (value) => {
  if (selectedRepository.value && value === selectedRepository.value.path) {
    return;
  }

  selectedRepositoryPath.value = null;
  highlightedRepoIndex.value = 0;
});

watch(
  [enabledHarnesses, defaultHarnessType],
  () => {
    if (enabledHarnesses.value.length === 0 && selectedHarnessType.value === defaultHarnessType.value) {
      return;
    }

    if (enabledHarnesses.value.some((harness) => harness.type === selectedHarnessType.value)) {
      return;
    }

    selectedHarnessType.value = getPreferredHarnessType();
  },
  { immediate: true },
);

// Apply initial source on mount
applyInitialSource();

// Clear the preset from store on unmount
onUnmounted(() => {
  workspaceUiStore.setNewSessionInitialSource(null);
});
</script>

<template>
  <div
    class="flex h-full flex-col overflow-y-auto"
    @keydown="handleKeydown"
  >
    <div class="mx-auto w-full max-w-[560px] px-6 py-8">
      <div class="mb-6">
        <h1 class="text-2xl font-semibold text-foreground">
          New Session
        </h1>
        <p class="mt-1 text-sm text-muted-foreground">
          Pick a repo and go — everything else is optional
        </p>
      </div>

      <form
        class="space-y-6"
        @submit.prevent="handleSubmit"
      >
        <!-- GitHub context card -->
        <div
          v-if="activeGitHubPreset"
          class="flex flex-wrap items-start justify-between gap-3 border-2 border-dashed border-border bg-muted/20 p-4"
        >
          <div class="min-w-0 flex-1 space-y-2">
            <a
              :href="activeGitHubPreset.htmlUrl"
              target="_blank"
              rel="noreferrer noopener"
              class="inline-flex max-w-full items-center gap-1 text-sm font-medium text-primary hover:underline"
            >
              <span class="truncate">{{ activeGitHubPreset.htmlUrl }}</span>
              <ExternalLink class="h-3.5 w-3.5 shrink-0" />
            </a>

            <p class="text-sm font-medium text-foreground">
              GitHub {{ activeGitHubPreset.sourceType === 'github-pull-request' ? 'pull request' : 'issue' }} context
            </p>
            <p class="text-sm text-muted-foreground">
              {{ activeGitHubPreset.repoFullName }} #{{ activeGitHubPreset.number }}
            </p>
            <p class="text-sm text-muted-foreground">
              {{ activeGitHubPreset.title }}
            </p>

            <p
              v-if="gitHubContextPreview"
              class="border border-border/60 bg-background/70 px-3 py-2 text-sm text-muted-foreground"
            >
              {{ gitHubContextPreview }}
            </p>
          </div>

          <Button
            type="button"
            variant="ghost"
            size="sm"
            :disabled="isCreating"
            @click="clearGitHubPreset"
          >
            Clear
          </Button>
        </div>

        <!-- Where to run -->
        <div class="space-y-3">
          <label class="block text-base font-semibold text-foreground">Where to run</label>

          <div class="grid gap-3">
            <button
              type="button"
              :class="cn(
                'flex flex-col items-start gap-2 border-2 p-4 text-left transition-colors',
                whereToRunMode === 'new-worktree'
                  ? 'border-primary bg-primary/5'
                  : 'border-border hover:border-border/80 hover:bg-muted/30',
              )"
              @click="whereToRunMode = 'new-worktree'"
            >
              <div class="flex items-center gap-2">
                <div
                  :class="cn(
                    'flex h-4 w-4 items-center justify-center rounded-full border-2',
                    whereToRunMode === 'new-worktree'
                      ? 'border-primary bg-primary'
                      : 'border-muted-foreground',
                  )"
                >
                  <div
                    v-if="whereToRunMode === 'new-worktree'"
                    class="h-2 w-2 rounded-full bg-primary-foreground"
                  />
                </div>
                <span class="font-medium text-foreground">New worktree</span>
              </div>
              <p class="ml-6 text-sm text-muted-foreground">
                Creates an isolated worktree — ideal for parallel work
              </p>
            </button>

            <button
              type="button"
              :class="cn(
                'flex flex-col items-start gap-2 border-2 p-4 text-left transition-colors',
                whereToRunMode === 'existing-worktree'
                  ? 'border-primary bg-primary/5'
                  : 'border-border hover:border-border/80 hover:bg-muted/30',
              )"
              @click="whereToRunMode = 'existing-worktree'"
            >
              <div class="flex items-center gap-2">
                <div
                  :class="cn(
                    'flex h-4 w-4 items-center justify-center rounded-full border-2',
                    whereToRunMode === 'existing-worktree'
                      ? 'border-primary bg-primary'
                      : 'border-muted-foreground',
                  )"
                >
                  <div
                    v-if="whereToRunMode === 'existing-worktree'"
                    class="h-2 w-2 rounded-full bg-primary-foreground"
                  />
                </div>
                <span class="font-medium text-foreground">Existing worktree</span>
              </div>
              <p class="ml-6 text-sm text-muted-foreground">
                Use a worktree that already exists
              </p>
            </button>

            <button
              type="button"
              :class="cn(
                'flex flex-col items-start gap-2 border-2 p-4 text-left transition-colors',
                whereToRunMode === 'repository'
                  ? 'border-primary bg-primary/5'
                  : 'border-border hover:border-border/80 hover:bg-muted/30',
              )"
              @click="whereToRunMode = 'repository'"
            >
              <div class="flex items-center gap-2">
                <div
                  :class="cn(
                    'flex h-4 w-4 items-center justify-center rounded-full border-2',
                    whereToRunMode === 'repository'
                      ? 'border-primary bg-primary'
                      : 'border-muted-foreground',
                  )"
                >
                  <div
                    v-if="whereToRunMode === 'repository'"
                    class="h-2 w-2 rounded-full bg-primary-foreground"
                  />
                </div>
                <span class="font-medium text-foreground">Repository</span>
              </div>
              <p class="ml-6 text-sm text-muted-foreground">
                Work directly in the repo — no isolation
              </p>
            </button>

            <button
              v-if="!isCloudMode"
              type="button"
              :disabled="Boolean(activeGitHubPreset)"
              :class="cn(
                'flex flex-col items-start gap-2 border-2 p-4 text-left transition-colors',
                whereToRunMode === 'directory'
                  ? 'border-primary bg-primary/5'
                  : 'border-border hover:border-border/80 hover:bg-muted/30',
                activeGitHubPreset ? 'cursor-not-allowed opacity-60' : '',
              )"
              @click="whereToRunMode = 'directory'"
            >
              <div class="flex items-center gap-2">
                <div
                  :class="cn(
                    'flex h-4 w-4 items-center justify-center rounded-full border-2',
                    whereToRunMode === 'directory'
                      ? 'border-primary bg-primary'
                      : 'border-muted-foreground',
                  )"
                >
                  <div
                    v-if="whereToRunMode === 'directory'"
                    class="h-2 w-2 rounded-full bg-primary-foreground"
                  />
                </div>
                <span class="font-medium text-foreground">Directory</span>
              </div>
              <p class="ml-6 text-sm text-muted-foreground">
                Choose any folder on disk
              </p>
            </button>
          </div>
        </div>

        <!-- Repository picker (for repo-based modes) -->
        <div
          v-if="sourceKind === 'repository'"
          class="space-y-2"
        >
          <label
            for="new-session-repository"
            class="block text-sm font-medium text-foreground"
          >Repository</label>

          <div class="relative">
            <Input
              id="new-session-repository"
              v-model="repositoryQuery"
              autocomplete="off"
              placeholder="Type to filter repositories..."
              :disabled="isCreating || isRepositoriesLoading"
              @focus="isRepositoryListOpen = true; highlightedRepoIndex = 0"
              @blur="handleRepositoryBlur"
              @keydown="handleRepositoryKeydown"
            />

            <LoaderCircle
              v-if="isRepositoriesLoading"
              class="absolute top-1/2 right-3 h-4 w-4 -translate-y-1/2 animate-spin text-muted-foreground"
            />

            <div
              v-if="isRepositoryListOpen && !isRepositoriesLoading"
              class="absolute z-50 mt-1 max-h-64 w-full overflow-auto border border-border bg-card shadow-xl"
            >
              <button
                v-for="(repository, index) in filteredRepositories"
                :key="repository.path"
                type="button"
                :data-repo-highlighted="index === highlightedRepoIndex"
                :class="cn(
                  'flex w-full items-start justify-between gap-3 px-3 py-2 text-left text-sm hover:bg-accent',
                  index === highlightedRepoIndex ? 'bg-accent' : '',
                )"
                @mousedown.prevent="selectRepository(repository)"
                @mouseenter="highlightedRepoIndex = index"
              >
                <span class="min-w-0 flex-1">
                  <span class="block truncate font-medium font-mono text-xs">{{ repository.name }}</span>
                  <span class="block truncate text-[10px] text-muted-foreground">{{ repository.path }}</span>
                </span>

                <Check
                  v-if="selectedRepositoryPath === repository.path"
                  class="mt-0.5 h-4 w-4 shrink-0 text-primary"
                />
              </button>

              <p
                v-if="filteredRepositories.length === 0"
                class="px-3 py-2 text-sm text-muted-foreground"
              >
                No repositories match your search.
              </p>
            </div>
          </div>
        </div>

        <!-- Branch field (for new worktree) -->
        <div
          v-if="whereToRunMode === 'new-worktree'"
          class="space-y-2"
        >
          <label
            for="new-session-branch"
            class="block text-sm font-medium text-foreground"
          >
            Branch <span class="font-normal text-muted-foreground">(optional)</span>
          </label>
          <Input
            id="new-session-branch"
            :model-value="effectiveBranch"
            :placeholder="generatedBranch"
            :disabled="isCreating"
            :class="cn(!branchManuallyEdited && 'italic text-muted-foreground')"
            @update:model-value="(value) => {
              branch = String(value);
              branchManuallyEdited = true;
            }"
          />
          <p class="text-xs text-muted-foreground">
            Auto-generated from title. Edit to override.
          </p>
        </div>

        <!-- Existing worktree picker -->
        <div
          v-if="whereToRunMode === 'existing-worktree'"
          class="space-y-2"
        >
          <label
            for="existing-worktree-select"
            class="block text-sm font-medium text-foreground"
          >Worktree</label>
          <div
            v-if="isWorktreesLoading"
            class="flex items-center gap-2 text-xs text-muted-foreground"
          >
            <LoaderCircle class="h-3.5 w-3.5 animate-spin" />
            Loading worktrees…
          </div>
          <div
            v-else-if="!selectedRepositoryPath || worktrees.length === 0"
            class="text-xs text-muted-foreground"
          >
            {{ !selectedRepositoryPath ? 'Select a repository first.' : 'No linked worktrees found for this repository.' }}
          </div>
          <Select
            v-else
            id="existing-worktree-select"
            :model-value="selectedWorktreePath ?? ''"
            :disabled="isCreating"
            @update:model-value="(v) => { selectedWorktreePath = String(v) || null; }"
          >
            <SelectTrigger class="w-full">
              <SelectValue placeholder="Select a worktree…" />
            </SelectTrigger>
            <SelectContent>
              <SelectItem
                v-for="wt in worktrees"
                :key="wt.path"
                :value="wt.path"
              >
                {{ wt.branch ?? wt.path }}
              </SelectItem>
            </SelectContent>
          </Select>
        </div>

        <!-- Directory picker -->
        <div
          v-if="sourceKind === 'directory'"
          class="space-y-2"
        >
          <label
            for="new-session-directory"
            class="block text-sm font-medium text-foreground"
          >Directory</label>

          <DirectoryPickerPopover
            :browser="directoryBrowser"
            :open="isDirectoryPickerOpen"
            mode="navigate"
            align="end"
            content-class="w-[25rem]"
            @update:open="handleDirectoryPickerOpenChange"
            @select="handleDirectorySelected"
          >
            <template #trigger>
              <div class="flex gap-2">
                <Input
                  id="new-session-directory"
                  v-model="directory"
                  placeholder="/path/to/project"
                  :disabled="isCreating"
                  class="flex-1"
                />

                <Button
                  type="button"
                  variant="outline"
                  size="icon"
                  class="shrink-0"
                  :disabled="isCreating"
                >
                  <Folder class="h-4 w-4" />
                </Button>
              </div>
            </template>
          </DirectoryPickerPopover>
        </div>

        <!-- More options collapsible -->
        <Collapsible v-model:open="isMoreOptionsOpen">
          <CollapsibleTrigger as-child>
            <Button
              type="button"
              variant="ghost"
              class="flex w-full items-center justify-between px-0 hover:bg-transparent"
            >
              <span class="text-sm font-medium">More options</span>
              <ChevronDown
                :class="cn(
                  'h-4 w-4 transition-transform',
                  isMoreOptionsOpen && 'rotate-180',
                )"
              />
            </Button>
          </CollapsibleTrigger>

          <CollapsibleContent class="space-y-4 pt-4">
            <!-- Title -->
            <div class="space-y-2">
              <label
                for="session-title"
                class="block text-sm font-medium text-foreground"
              >
                Title <span class="font-normal text-muted-foreground">(optional)</span>
              </label>
              <Input
                id="session-title"
                v-model="title"
                placeholder="What are you working on?"
                :disabled="isCreating"
              />
            </div>

            <!-- Project -->
            <div
              v-if="showProjectSelect"
              class="space-y-2"
            >
              <label
                for="new-session-project"
                class="block text-sm font-medium text-foreground"
              >Project</label>

              <Select
                v-model="selectedProjectId"
                :disabled="isCreating"
              >
                <SelectTrigger
                  id="new-session-project"
                  class="w-full"
                >
                  <SelectValue placeholder="Ungrouped" />
                </SelectTrigger>

                <SelectContent>
                  <SelectItem :value="UNGROUPED_PROJECT_ID">
                    Ungrouped
                  </SelectItem>
                  <SelectItem
                    v-for="project in userProjects"
                    :key="project.id"
                    :value="project.id"
                  >
                    {{ project.name }}
                  </SelectItem>
                </SelectContent>
              </Select>
            </div>

            <!-- Tags -->
            <div class="space-y-2">
              <label
                for="new-session-tags"
                class="block text-sm font-medium text-foreground"
              >
                Tags <span class="font-normal text-muted-foreground">(optional)</span>
              </label>
              <Input
                id="new-session-tags"
                :model-value="tags.join(', ')"
                placeholder="e.g. review-requested, deploy"
                :disabled="isCreating"
                @update:model-value="(value) => {
                  tags = String(value)
                    .split(',')
                    .map(tag => tag.trim())
                    .filter(tag => tag.length > 0);
                }"
              />
              <p class="text-xs text-muted-foreground">
                Comma-separated tags for organizing sessions.
              </p>
            </div>

            <!-- Harness -->
            <div
              v-if="showHarnessSelect"
              class="space-y-2"
            >
              <label
                for="new-session-harness"
                class="block text-sm font-medium text-foreground"
              >Harness</label>

              <Select
                v-model="selectedHarnessType"
                :disabled="isCreating"
              >
                <SelectTrigger
                  id="new-session-harness"
                  class="w-full"
                >
                  <SelectValue placeholder="Select a harness" />
                </SelectTrigger>

                <SelectContent>
                  <SelectItem
                    v-for="harness in enabledHarnesses"
                    :key="harness.type"
                    :value="harness.type"
                  >
                    {{ harness.displayName }}
                  </SelectItem>
                </SelectContent>
              </Select>
            </div>
          </CollapsibleContent>
        </Collapsible>

        <!-- Error banner -->
        <div
          v-if="formError"
          data-testid="new-session-error"
          class="flex items-start gap-3 border border-destructive/30 bg-destructive/10 px-4 py-3 text-sm text-destructive"
          role="alert"
        >
          <AlertCircle class="mt-0.5 h-4 w-4 shrink-0" />
          <p>{{ formError }}</p>
        </div>

        <!-- Action bar -->
        <div class="flex items-center justify-between gap-3 border-t border-border pt-6">
          <Button
            type="button"
            variant="ghost"
            :disabled="isCreating"
            @click="handleCancel"
          >
            Cancel
            <span class="ml-2 text-xs text-muted-foreground">Esc</span>
          </Button>

          <Button
            type="submit"
            data-testid="create-session-submit"
            :disabled="!canSubmit"
          >
            <LoaderCircle
              v-if="isCreating"
              class="h-4 w-4 animate-spin"
            />
            {{ isCreating ? "Spawning…" : "Create Session" }}
            <span
              v-if="!isCreating"
              class="ml-2 text-xs opacity-70"
            >⌘↵</span>
          </Button>
        </div>
      </form>
    </div>
  </div>
</template>
