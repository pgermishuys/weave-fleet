<script setup lang="ts">
import { computed, nextTick, shallowRef, watch } from "vue";
import { AlertCircle, Check, ExternalLink, Folder, FolderGit2, LoaderCircle } from "lucide-vue-next";
import { storeToRefs } from "pinia";
import { Button } from "@/components/ui/button";
import {
  Dialog,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import { Input } from "@/components/ui/input";
import DirectoryPickerPopover from "@/components/ui/DirectoryPickerPopover.vue";
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select";
import { useDirectoryBrowser } from "@/composables/use-directory-browser";
import { useHarnesses } from "@/composables/use-harnesses";
import { useProjects } from "@/composables/use-projects";
import { useRepositories } from "@/composables/use-repositories";
import { useCreateSession } from "@/composables/use-session-actions";
import { useWorktrees } from "@/composables/use-worktrees";
import type {
  CreateSessionResponse,
  HarnessInfo,
  ProjectResponse,
  ScannedRepository,
  SessionSourceSelection,
} from "@/lib/api-types";
import {
  buildGitHubSessionSourceSelection,
  findRepositoryForGitHubPreset,
  type GitHubSessionSourcePreset,
} from "@/lib/github-session-source";
import { cn } from "@/lib/utils";
import { useAppShellStore } from "@/stores/app-shell";
import { usePreferencesStore } from "@/stores/preferences";

type SessionSourceKind = "repository" | "directory";
type IsolationStrategy = "existing" | "worktree";

interface Props {
  initialProjectId?: string | null;
  initialSource?: GitHubSessionSourcePreset | null;
  createEndpoint?: string;
}

const UNGROUPED_PROJECT_ID = "__ungrouped__";

const props = withDefaults(defineProps<Props>(), {
  initialProjectId: null,
  initialSource: null,
  createEndpoint: "/api/sessions",
});

const open = defineModel<boolean>("open", { default: false });

const emit = defineEmits<{
  created: [response: CreateSessionResponse];
}>();

const appShellStore = useAppShellStore();
const preferencesStore = usePreferencesStore();
const { config } = storeToRefs(appShellStore);

type WorktreeMode = "new" | "existing";

preferencesStore.ensureLoaded();

const sourceKind = shallowRef<SessionSourceKind>("repository");
const repositoryQuery = shallowRef("");
const selectedRepositoryPath = shallowRef<string | null>(null);
const isRepositoryListOpen = shallowRef(false);
const highlightedRepoIndex = shallowRef(0);
const isDirectoryPickerOpen = shallowRef(false);
const directory = shallowRef("");
const title = shallowRef("");
const isolationStrategy = shallowRef<IsolationStrategy>("worktree");
const branch = shallowRef("");
const branchManuallyEdited = shallowRef(false);
const worktreeMode = shallowRef<WorktreeMode>("new");
const selectedWorktreePath = shallowRef<string | null>(null);
const selectedProjectId = shallowRef(props.initialProjectId ?? UNGROUPED_PROJECT_ID);
function getDefaultHarnessType(): string {
  return preferencesStore.get("defaultHarnessType", "opencode");
}

const selectedHarnessType = shallowRef(getDefaultHarnessType());
const submitAttempted = shallowRef(false);
const activeGitHubPreset = shallowRef<GitHubSessionSourcePreset | null>(null);

const {
  repositories,
  isLoading: isRepositoriesLoading,
  error: repositoriesError,
} = useRepositories();
const {
  projects,
  error: projectsError,
} = useProjects({ enabled: open });
const {
  harnesses,
  error: harnessesError,
} = useHarnesses();
const {
  createSession,
  isLoading: isCreating,
  error: createError,
} = useCreateSession(props.createEndpoint);
const directoryBrowser = useDirectoryBrowser();
const {
  worktrees,
  isLoading: isWorktreesLoading,
} = useWorktrees({ repositoryPath: selectedRepositoryPath, enabled: open });

const userProjects = computed<readonly ProjectResponse[]>(() => {
  return projects.value.filter((project) => project.type !== "scratch");
});

const availableHarnesses = computed<readonly HarnessInfo[]>(() => {
  return harnesses.value.filter((harness) => harness.available);
});

const showHarnessSelect = computed(() => availableHarnesses.value.length > 1);
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

const generatedBranch = computed(() => {
  return title.value
    .toLowerCase()
    .trim()
    .replace(/[^a-z0-9\s-]/g, "")
    .replace(/\s+/g, "-")
    .replace(/-+/g, "-")
    .replace(/^-|-$/g, "");
});

const effectiveBranch = computed(() => {
  if (isolationStrategy.value !== "worktree") {
    return "";
  }

  return branchManuallyEdited.value ? branch.value.trim() : generatedBranch.value;
});

const resolvedHarnessType = computed(() => {
  if (selectedHarnessType.value && availableHarnesses.value.some((harness) => harness.type === selectedHarnessType.value)) {
    return selectedHarnessType.value;
  }

  return availableHarnesses.value[0]?.type ?? "";
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

const dialogError = computed(() => {
  if (submitAttempted.value && validationMessage.value) {
    return validationMessage.value;
  }

  return createError.value
    ?? repositoriesError.value
    ?? projectsError.value
    ?? harnessesError.value
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

function getInitialProjectSelection(): string {
  return props.initialProjectId ?? UNGROUPED_PROJECT_ID;
}

function resetForm(): void {
  sourceKind.value = "repository";
  repositoryQuery.value = "";
  selectedRepositoryPath.value = null;
  isRepositoryListOpen.value = false;
  isDirectoryPickerOpen.value = false;
  directory.value = "";
  title.value = "";
  isolationStrategy.value = "worktree";
  branch.value = "";
  branchManuallyEdited.value = false;
  worktreeMode.value = "new";
  selectedWorktreePath.value = null;
  selectedProjectId.value = getInitialProjectSelection();
  selectedHarnessType.value = getDefaultHarnessType();
  submitAttempted.value = false;
  activeGitHubPreset.value = null;
}

function applyInitialSource(): void {
  activeGitHubPreset.value = props.initialSource;

  if (!props.initialSource) {
    return;
  }

  sourceKind.value = "repository";
  title.value = props.initialSource.title;
  isolationStrategy.value = "worktree";
  branch.value = props.initialSource.suggestedBranch?.trim() ?? "";
  branchManuallyEdited.value = Boolean(props.initialSource.suggestedBranch?.trim());
  worktreeMode.value = "new";
  selectedWorktreePath.value = null;
}

function clearGitHubPreset(): void {
  activeGitHubPreset.value = null;
}

function selectRepository(repository: ScannedRepository): void {
  selectedRepositoryPath.value = repository.path;
  repositoryQuery.value = repository.path;
  isRepositoryListOpen.value = false;
}

function handleOpenChange(value: boolean): void {
  open.value = value;
}

function handleSourceToggle(kind: SessionSourceKind): void {
  if (kind === "directory" && (isCloudMode.value || activeGitHubPreset.value)) {
    return;
  }

  sourceKind.value = kind;
  nextTick(() => {
    document.querySelector<HTMLElement>('[role="radiogroup"] [tabindex="0"]')?.focus();
  });
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
    // Close list and let default tab behavior proceed
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

function handleIsolationToggle(strategy: IsolationStrategy): void {
  isolationStrategy.value = strategy;
  if (strategy !== "worktree") {
    worktreeMode.value = "new";
    selectedWorktreePath.value = null;
  }
  nextTick(() => {
    document.querySelector<HTMLElement>('[aria-label="Isolation Strategy"] [tabindex="0"]')?.focus();
  });
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

function handleDialogInteractOutside(event: Event): void {
  const target = event.target as HTMLElement | null;
  if (target?.closest('[data-slot="popover-content"]')) {
    event.preventDefault();
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
    });

    open.value = false;
    emit("created", response);
  } catch {
    // Mutation state drives the dialog error banner.
  }
}

watch(open, async (isOpen) => {
  if (isOpen) {
    submitAttempted.value = false;
    selectedProjectId.value = getInitialProjectSelection();
    applyInitialSource();
    await nextTick();
    document.querySelector<HTMLButtonElement>('[data-testid="new-session-dialog"] fieldset button')?.focus();
    return;
  }

  resetForm();
});

watch(
  [open, sourceKind, repositories, activeGitHubPreset],
  ([isOpen, nextSourceKind, nextRepositories, nextGitHubPreset]) => {
    if (!isOpen || nextSourceKind !== "repository" || selectedRepositoryPath.value || nextRepositories.length === 0) {
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

watch(sourceKind, (nextSourceKind) => {
  if (nextSourceKind !== "repository") {
    if (activeGitHubPreset.value) {
      sourceKind.value = "repository";
      return;
    }

    isRepositoryListOpen.value = false;
    syncDirectoryBrowser();
    return;
  }

  isDirectoryPickerOpen.value = false;
});

watch(repositoryQuery, (value) => {
  if (selectedRepository.value && value === selectedRepository.value.path) {
    return;
  }

  selectedRepositoryPath.value = null;
  highlightedRepoIndex.value = 0;
});

watch(
  () => props.initialSource,
  (nextInitialSource) => {
    if (!open.value) {
      return;
    }

    activeGitHubPreset.value = nextInitialSource;
  },
);
</script>

<template>
  <Dialog
    :open="open"
    @update:open="handleOpenChange"
  >
    <DialogContent
      class="sm:max-w-md top-[5%] translate-y-0"
      data-testid="new-session-dialog"
      @interact-outside="handleDialogInteractOutside"
    >
      <DialogHeader>
        <DialogTitle>New Session</DialogTitle>
        <DialogDescription v-if="activeGitHubPreset">
          Start a session from a repository with GitHub context.
        </DialogDescription>
      </DialogHeader>

      <form
        class="space-y-5"
        @submit.prevent="handleSubmit"
      >
        <div
          v-if="activeGitHubPreset"
          class="flex flex-wrap items-start justify-between gap-3 rounded-lg border border-border bg-muted/20 p-3"
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
              class="rounded-md border border-border/60 bg-background/70 px-3 py-2 text-sm text-muted-foreground"
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

        <div class="space-y-3">
          <span class="text-sm font-medium text-foreground">
            Source
          </span>

          <div
            class="flex gap-3"
            role="radiogroup"
            aria-label="Source"
            @keydown.left.prevent="handleSourceToggle('repository')"
            @keydown.right.prevent="handleSourceToggle('directory')"
          >
            <button
              type="button"
              role="radio"
              :aria-checked="sourceKind === 'repository'"
              :tabindex="sourceKind === 'repository' ? 0 : -1"
              :class="cn(
                'inline-flex flex-1 items-center justify-center gap-2 rounded-md border px-4 py-2 text-xs font-medium transition-colors',
                sourceKind === 'repository'
                  ? 'border-primary bg-primary/10 text-primary'
                  : 'border-border text-muted-foreground hover:text-foreground',
              )"
              @click="sourceKind = 'repository'"
            >
              <FolderGit2 class="h-3.5 w-3.5" />
              Repository
            </button>

            <button
              v-if="!isCloudMode"
              type="button"
              role="radio"
              :aria-checked="sourceKind === 'directory'"
              :tabindex="sourceKind === 'directory' ? 0 : -1"
              :disabled="Boolean(activeGitHubPreset)"
              :class="cn(
                'inline-flex flex-1 items-center justify-center gap-2 rounded-md border px-4 py-2 text-xs font-medium transition-colors',
                sourceKind === 'directory'
                  ? 'border-primary bg-primary/10 text-primary'
                  : 'border-border text-muted-foreground hover:text-foreground',
                activeGitHubPreset ? 'cursor-not-allowed opacity-60' : '',
              )"
              @click="sourceKind = 'directory'"
            >
              <Folder class="h-3.5 w-3.5" />
              Directory
            </button>
          </div>
        </div>

        <div
          v-if="sourceKind === 'repository'"
          class="space-y-5"
        >
          <div class="space-y-2">
            <label
              for="new-session-repository"
              class="text-sm font-medium text-foreground"
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
                class="absolute z-50 mt-1 max-h-64 w-full overflow-auto rounded-md border border-border shadow-xl shadow-black/50 ring-1 ring-white/[0.08]"
                :style="{ backgroundColor: 'color-mix(in srgb, var(--card-bg) 100%, white 4%)' }"
              >
                <button
                  v-for="(repository, index) in filteredRepositories"
                  :key="repository.path"
                  type="button"
                  :data-repo-highlighted="index === highlightedRepoIndex"
                  :class="cn(
                    'flex w-full items-start justify-between gap-3 rounded-sm px-3 py-2 text-left text-sm hover:bg-white/[0.06]',
                    index === highlightedRepoIndex ? 'bg-white/[0.10]' : '',
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

          <div class="space-y-2">
            <span class="text-sm font-medium text-foreground">Isolation Strategy</span>

            <div
              class="flex gap-3"
              role="radiogroup"
              aria-label="Isolation Strategy"
              @keydown.left.prevent="handleIsolationToggle('worktree')"
              @keydown.right.prevent="handleIsolationToggle('existing')"
            >
              <button
                type="button"
                role="radio"
                :aria-checked="isolationStrategy === 'worktree'"
                :tabindex="isolationStrategy === 'worktree' ? 0 : -1"
                :disabled="isCreating"
                :class="cn(
                  'inline-flex flex-1 items-center justify-center gap-2 rounded-md border px-4 py-2 text-xs font-medium transition-colors',
                  isolationStrategy === 'worktree'
                    ? 'border-primary bg-primary/10 text-primary'
                    : 'border-border text-muted-foreground hover:text-foreground',
                )"
                @click="isolationStrategy = 'worktree'"
              >
                <FolderGit2 class="h-3.5 w-3.5" />
                Worktree
              </button>

              <button
                type="button"
                role="radio"
                :aria-checked="isolationStrategy === 'existing'"
                :tabindex="isolationStrategy === 'existing' ? 0 : -1"
                :disabled="isCreating"
                :class="cn(
                  'inline-flex flex-1 items-center justify-center gap-2 rounded-md border px-4 py-2 text-xs font-medium transition-colors',
                  isolationStrategy === 'existing'
                    ? 'border-primary bg-primary/10 text-primary'
                    : 'border-border text-muted-foreground hover:text-foreground',
                )"
                @click="isolationStrategy = 'existing'"
              >
                <Folder class="h-3.5 w-3.5" />
                Directory
              </button>
            </div>

            <p class="text-xs text-muted-foreground opacity-50">
              {{ isolationStrategy === 'worktree'
                ? 'Creates a git worktree — ideal for parallel work on the same repo.'
                : 'Use the repository directory as-is. Simple, no copy or branch.'
              }}
            </p>
          </div>

          <div class="space-y-2">
            <label
              for="session-title"
              class="text-sm font-medium text-foreground"
            >Title <span class="font-normal text-muted-foreground">(optional)</span></label>
            <Input
              id="session-title"
              v-model="title"
              placeholder="What are you working on?"
              :disabled="isCreating"
            />
          </div>

          <div
            v-if="isolationStrategy === 'worktree'"
            class="space-y-2"
          >
            <!-- Sub-toggle: New worktree vs Existing worktree -->
            <div class="flex gap-2">
              <button
                type="button"
                :disabled="isCreating"
                :class="cn(
                  'inline-flex flex-1 items-center justify-center gap-1.5 rounded border px-3 py-1.5 text-xs font-medium transition-colors',
                  worktreeMode === 'new'
                    ? 'border-primary bg-primary/10 text-primary'
                    : 'border-border text-muted-foreground hover:text-foreground',
                )"
                @click="worktreeMode = 'new'; selectedWorktreePath = null"
              >
                New
              </button>
              <button
                type="button"
                :disabled="isCreating"
                :class="cn(
                  'inline-flex flex-1 items-center justify-center gap-1.5 rounded border px-3 py-1.5 text-xs font-medium transition-colors',
                  worktreeMode === 'existing'
                    ? 'border-primary bg-primary/10 text-primary'
                    : 'border-border text-muted-foreground hover:text-foreground',
                )"
                @click="worktreeMode = 'existing'; selectedWorktreePath = null"
              >
                Existing
              </button>
            </div>

            <!-- New worktree: branch input -->
            <div
              v-if="worktreeMode === 'new'"
              class="space-y-2"
            >
              <label
                for="new-session-branch"
                class="text-sm font-medium text-foreground"
              >Branch <span class="font-normal text-muted-foreground">(optional)</span></label>
              <Input
                id="new-session-branch"
                :model-value="effectiveBranch"
                placeholder="feature/my-branch"
                :disabled="isCreating"
                @update:model-value="(value) => {
                  branch = String(value);
                  branchManuallyEdited = true;
                }"
              />
              <p class="text-xs text-muted-foreground opacity-50">
                Auto-generated from title. Edit to override.
              </p>
            </div>

            <!-- Existing worktree: picker -->
            <div
              v-else
              class="space-y-2"
            >
              <label
                for="existing-worktree-select"
                class="text-sm font-medium text-foreground"
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
                class="text-xs text-muted-foreground opacity-50"
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
                <SelectTrigger class="w-full text-xs">
                  <SelectValue placeholder="Select a worktree…" />
                </SelectTrigger>
                <SelectContent>
                  <SelectItem
                    v-for="wt in worktrees"
                    :key="wt.path"
                    :value="wt.path"
                    class="text-xs"
                  >
                    {{ wt.branch ?? wt.path }}
                  </SelectItem>
                </SelectContent>
              </Select>
            </div>
          </div>
        </div>

        <div
          v-else-if="sourceKind === 'directory'"
          class="space-y-5"
        >
          <div class="space-y-2">
            <label
              for="new-session-directory"
              class="text-sm font-medium text-foreground"
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

          <div class="space-y-2">
            <label
              for="session-title"
              class="text-sm font-medium text-foreground"
            >Title <span class="font-normal text-muted-foreground">(optional)</span></label>
            <Input
              id="session-title"
              v-model="title"
              placeholder="What are you working on?"
              :disabled="isCreating"
            />
          </div>
        </div>

        <div class="space-y-5">
          <div
            v-if="showProjectSelect"
            class="space-y-2"
          >
            <label
              for="new-session-project"
              class="text-sm font-medium text-foreground"
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

          <div
            v-if="showHarnessSelect"
            class="space-y-2"
          >
            <label
              for="new-session-harness"
              class="text-sm font-medium text-foreground"
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
                  v-for="harness in availableHarnesses"
                  :key="harness.type"
                  :value="harness.type"
                >
                  {{ harness.displayName }}
                </SelectItem>
              </SelectContent>
            </Select>
          </div>
        </div>

        <div
          v-if="dialogError"
          data-testid="new-session-error"
          class="flex items-start gap-3 rounded-md border border-destructive/30 bg-destructive/10 px-4 py-3 text-sm text-destructive"
          role="alert"
        >
          <AlertCircle class="mt-0.5 h-4 w-4 shrink-0" />
          <p>{{ dialogError }}</p>
        </div>

        <DialogFooter>
          <Button
            type="button"
            variant="outline"
            :disabled="isCreating"
            @click="open = false"
          >
            Cancel
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
          </Button>
        </DialogFooter>
      </form>
    </DialogContent>
  </Dialog>
</template>
