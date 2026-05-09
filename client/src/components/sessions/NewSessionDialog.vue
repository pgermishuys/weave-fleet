<script setup lang="ts">
import { computed, nextTick, shallowRef, watch } from "vue";
import { AlertCircle, ArrowUp, Check, ExternalLink, Folder, FolderGit2, LoaderCircle, RefreshCw } from "lucide-vue-next";
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
import { Popover, PopoverContent, PopoverTrigger } from "@/components/ui/popover";
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
import { readWorkspacePreferences } from "@/lib/workspace-preferences";
import { cn } from "@/lib/utils";
import { useAppShellStore } from "@/stores/app-shell";

type SessionSourceKind = "repository" | "directory";
type IsolationStrategy = "existing" | "worktree" | "clone";

interface Props {
  initialProjectId?: string | null;
  initialSource?: GitHubSessionSourcePreset | null;
}

const UNGROUPED_PROJECT_ID = "__ungrouped__";

const props = withDefaults(defineProps<Props>(), {
  initialProjectId: null,
  initialSource: null,
});

const open = defineModel<boolean>("open", { default: false });

const emit = defineEmits<{
  created: [response: CreateSessionResponse];
}>();

const appShellStore = useAppShellStore();
const { config } = storeToRefs(appShellStore);

const sourceKind = shallowRef<SessionSourceKind>("repository");
const repositoryQuery = shallowRef("");
const selectedRepositoryPath = shallowRef<string | null>(null);
const isRepositoryListOpen = shallowRef(false);
const isDirectoryPickerOpen = shallowRef(false);
const directory = shallowRef("");
const title = shallowRef("");
const isolationStrategy = shallowRef<IsolationStrategy>("worktree");
const branch = shallowRef("");
const branchManuallyEdited = shallowRef(false);
const selectedProjectId = shallowRef(props.initialProjectId ?? UNGROUPED_PROJECT_ID);
const selectedHarnessType = shallowRef("");
const submitAttempted = shallowRef(false);
const activeGitHubPreset = shallowRef<GitHubSessionSourcePreset | null>(null);

const {
  repositories,
  isLoading: isRepositoriesLoading,
  error: repositoriesError,
  refresh: refreshRepositories,
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
} = useCreateSession();
const {
  currentPath: directoryBrowserPath,
  entries: directoryEntries,
  isLoading: isDirectoryBrowserLoading,
  error: directoryBrowserError,
  roots: directoryRoots,
  parentPath: directoryParentPath,
  search: directorySearch,
  browse: browseDirectory,
  goUp: goUpDirectory,
  refresh: refreshDirectoryBrowser,
  setSearch: setDirectorySearch,
  hasActivated: hasActivatedDirectoryBrowser,
} = useDirectoryBrowser();

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

const directoryPickerLocation = computed(() => {
  if (directoryBrowserPath.value) {
    return directoryBrowserPath.value;
  }

  const preferredRoot = getPreferredWorkspaceRoot();
  if (preferredRoot) {
    return preferredRoot;
  }

  return directoryRoots.value[0] ?? "Workspace roots";
});

const filteredDirectoryEntries = computed(() => {
  const query = directorySearch.value.trim().toLowerCase();
  if (!query) {
    return directoryEntries.value;
  }

  return directoryEntries.value.filter((entry) => {
    const searchableText = `${entry.name} ${entry.path}`.toLowerCase();
    return searchableText.includes(query);
  });
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
        ...(isolationStrategy.value === "worktree" && effectiveBranch.value
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
    ?? directoryBrowserError.value
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
  selectedProjectId.value = getInitialProjectSelection();
  selectedHarnessType.value = "";
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

function getPreferredWorkspaceRoot(): string {
  if (typeof window === "undefined") {
    return "";
  }

  return readWorkspacePreferences(window.localStorage).preferredRootPath;
}

function getDirectoryBrowserStartPath(): string | null {
  const selectedDirectory = directory.value.trim();
  if (selectedDirectory) {
    return selectedDirectory;
  }

  return getPreferredWorkspaceRoot() || null;
}

function syncDirectoryBrowser(): void {
  const nextPath = getDirectoryBrowserStartPath();
  if (!hasActivatedDirectoryBrowser.value || directoryBrowserPath.value !== nextPath) {
    browseDirectory(nextPath);
  }
}

function openDirectoryPicker(): void {
  syncDirectoryBrowser();
  isDirectoryPickerOpen.value = true;
}

function handleDirectorySelected(path: string): void {
  directory.value = path;
  isDirectoryPickerOpen.value = false;
}

function handleDirectorySearchUpdate(value: string | number): void {
  setDirectorySearch(String(value));
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
    void refreshRepositories();
    await nextTick();
    document.getElementById("session-title")?.focus();
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

    selectRepository(nextRepositories[0]);
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
      class="sm:max-w-2xl"
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

          <fieldset
            class="space-y-3"
            aria-label="Source"
          >
          <legend class="text-sm font-medium text-foreground">
            Source
          </legend>

          <div class="inline-flex rounded-lg border border-border bg-muted/20 p-1">
            <button
              type="button"
              :class="cn(
                'inline-flex items-center gap-2 rounded-md px-4 py-2 text-sm font-medium transition-colors',
                sourceKind === 'repository'
                  ? 'bg-primary text-primary-foreground shadow-sm'
                  : 'text-muted-foreground hover:text-foreground',
              )"
              @click="sourceKind = 'repository'"
            >
              <FolderGit2 class="h-4 w-4" />
              Repository
            </button>

            <button
              v-if="!isCloudMode"
              type="button"
              :disabled="Boolean(activeGitHubPreset)"
              :class="cn(
                'inline-flex items-center gap-2 rounded-md px-4 py-2 text-sm font-medium transition-colors',
                sourceKind === 'directory'
                  ? 'bg-primary text-primary-foreground shadow-sm'
                  : 'text-muted-foreground hover:text-foreground',
                activeGitHubPreset ? 'cursor-not-allowed opacity-60' : '',
              )"
              @click="sourceKind = 'directory'"
            >
              <Folder class="h-4 w-4" />
              Directory
            </button>
          </div>
        </fieldset>

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
                @focus="isRepositoryListOpen = true"
                @blur="handleRepositoryBlur"
              />

              <LoaderCircle
                v-if="isRepositoriesLoading"
                class="absolute top-1/2 right-3 h-4 w-4 -translate-y-1/2 animate-spin text-muted-foreground"
              />

              <div
                v-if="isRepositoryListOpen && !isRepositoriesLoading"
                class="absolute z-50 mt-2 max-h-64 w-full overflow-auto rounded-md border border-border bg-popover p-1 shadow-md"
              >
                <button
                  v-for="repository in filteredRepositories"
                  :key="repository.path"
                  type="button"
                  class="flex w-full items-start justify-between gap-3 rounded-sm px-3 py-2 text-left text-sm hover:bg-accent hover:text-accent-foreground"
                  @mousedown.prevent="selectRepository(repository)"
                >
                  <span class="min-w-0 flex-1">
                    <span class="block truncate font-medium">{{ repository.name }}</span>
                    <span class="block truncate text-xs text-muted-foreground">{{ repository.path }}</span>
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
            <p class="text-xs text-muted-foreground">
              Optional session name.
            </p>
          </div>

          <div class="space-y-2">
            <label
              for="new-session-isolation"
              class="text-sm font-medium text-foreground"
            >Isolation Strategy</label>

            <Select
              v-model="isolationStrategy"
              :disabled="isCreating"
            >
              <SelectTrigger
                id="new-session-isolation"
                class="w-full"
              >
                <SelectValue placeholder="Select a strategy" />
              </SelectTrigger>

              <SelectContent>
                <SelectItem value="worktree">
                  Worktree
                </SelectItem>
                <SelectItem value="clone">
                  Clone
                </SelectItem>
                <SelectItem value="existing">
                  Existing
                </SelectItem>
              </SelectContent>
            </Select>
          </div>

          <div
            v-if="isolationStrategy === 'worktree'"
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
            <p class="text-xs text-muted-foreground">
              Auto-generated from title. Edit to override.
            </p>
          </div>
        </div>

        <div
          v-else
          class="space-y-2"
        >
          <label
            for="new-session-directory"
            class="text-sm font-medium text-foreground"
          >Directory</label>

          <div class="flex gap-2">
            <Input
              id="new-session-directory"
              v-model="directory"
              placeholder="/absolute/path/to/workspace"
              :disabled="isCreating"
            />

            <Popover v-model:open="isDirectoryPickerOpen">
              <PopoverTrigger as-child>
                <Button
                  type="button"
                  variant="outline"
                  :disabled="isCreating"
                  @click="openDirectoryPicker"
                >
                  <Folder class="h-4 w-4" />
                  Browse
                </Button>
              </PopoverTrigger>

              <PopoverContent
                align="end"
                class="w-[32rem] p-0"
                :style="{ backgroundColor: 'var(--card-bg)', opacity: '1' }"
              >
                <div class="border-b border-border bg-card-bg p-3">
                  <div class="flex items-start justify-between gap-3">
                    <div class="min-w-0">
                      <p class="text-xs font-medium uppercase tracking-wide text-muted-foreground">
                        Directory picker
                      </p>
                      <p class="truncate font-mono text-xs text-foreground">
                        {{ directoryPickerLocation }}
                      </p>
                    </div>

                    <div class="flex items-center gap-1">
                      <Button
                        type="button"
                        variant="ghost"
                        size="icon-sm"
                        :disabled="isDirectoryBrowserLoading || !directoryParentPath"
                        @click="goUpDirectory"
                      >
                        <ArrowUp class="h-4 w-4" />
                      </Button>

                      <Button
                        type="button"
                        variant="ghost"
                        size="icon-sm"
                        :disabled="isDirectoryBrowserLoading"
                        @click="refreshDirectoryBrowser"
                      >
                        <RefreshCw :class="['h-4 w-4', isDirectoryBrowserLoading ? 'animate-spin' : '']" />
                      </Button>
                    </div>
                  </div>

                  <Input
                    class="mt-3"
                    :model-value="directorySearch"
                    placeholder="Search directories"
                    @update:model-value="handleDirectorySearchUpdate"
                  />

                  <div
                    v-if="directoryRoots.length > 1"
                    class="mt-3 flex flex-wrap gap-2"
                  >
                    <Button
                      v-for="root in directoryRoots"
                      :key="root"
                      type="button"
                      variant="outline"
                      size="sm"
                      class="max-w-full"
                      @click="browseDirectory(root)"
                    >
                      <span class="truncate font-mono text-xs">{{ root }}</span>
                    </Button>
                  </div>
                </div>

                <div class="max-h-72 overflow-y-auto bg-card-bg p-2">
                  <button
                    v-for="entry in filteredDirectoryEntries"
                    :key="entry.path"
                    type="button"
                    class="flex w-full items-start justify-between gap-3 rounded-sm px-3 py-2 text-left text-sm hover:bg-accent hover:text-accent-foreground"
                    @click="handleDirectorySelected(entry.path)"
                  >
                    <span class="min-w-0 flex-1">
                      <span class="flex items-center gap-2 font-medium">
                        <FolderGit2
                          v-if="entry.isGitRepo"
                          class="h-4 w-4 shrink-0"
                        />
                        <Folder
                          v-else
                          class="h-4 w-4 shrink-0"
                        />
                        <span class="truncate">{{ entry.name }}</span>
                      </span>
                      <span class="mt-1 block truncate font-mono text-xs text-muted-foreground">{{ entry.path }}</span>
                    </span>

                    <Check
                      v-if="directory.trim() === entry.path"
                      class="mt-0.5 h-4 w-4 shrink-0 text-primary"
                    />
                  </button>

                  <div
                    v-if="isDirectoryBrowserLoading"
                    class="flex items-center gap-2 px-3 py-6 text-sm text-muted-foreground"
                  >
                    <LoaderCircle class="h-4 w-4 animate-spin" />
                    <span>Loading directories…</span>
                  </div>

                  <p
                    v-else-if="filteredDirectoryEntries.length === 0"
                    class="px-3 py-6 text-sm text-muted-foreground"
                  >
                    No directories found.
                  </p>
                </div>
              </PopoverContent>
            </Popover>
          </div>

          <p class="text-xs text-muted-foreground">
            Type a path manually or browse from your configured workspace directory.
          </p>
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
