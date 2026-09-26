<script setup lang="ts">
import { computed, nextTick, shallowRef, useId, useTemplateRef, watch } from "vue";
import {
  ArrowLeft,
  Check,
  ChevronDown,
  Folder,
  FolderGit2,
  FolderOpen,
  FolderPlus,
  GitBranch,
  Github,
  LoaderCircle,
  MessageSquare,
  Plus,
  Search,
} from "lucide-vue-next";
import type { FolderInspection, ScannedRepository } from "@/api/client";
import { Button } from "@/components/ui/button";
import DirectoryPickerPopover from "@/components/ui/DirectoryPickerPopover.vue";
import { Popover, PopoverContent, PopoverTrigger } from "@/components/ui/popover";
import { useDirectoryBrowser } from "@/composables/use-directory-browser";
import {
  addFolderToFleet,
  cloneRepository,
  createFolder,
  FolderExistsError,
  folderForInspection,
  inspectFolder,
  listWorkspaceRoots,
  type CloneProgress,
  type NewFolder,
} from "@/lib/folder-access";
import { defaultNewFolderRoot, folderNameFrom, joinPath, parseCloneSource, rootContaining, type CloneSource } from "@/lib/new-folder";
import { tildePath } from "@/lib/new-session-plan";
import type { NewSessionFolder } from "@/lib/new-session-request";
import { useGitHubRepos } from "@/plugins/builtin/github/composables/use-github-repos";

const props = withDefaults(defineProps<{
  folder: NewSessionFolder | null;
  repositories: readonly ScannedRepository[];
  recentFolders: readonly NewSessionFolder[];
  /** Any folder on disk (not in cloud mode, not with a GitHub issue attached). */
  allowBrowse: boolean;
  /** No folder at all (not with a GitHub issue attached). */
  allowNone: boolean;
  /** Create a folder or clone a repository from the menu (not in cloud mode, not with a GitHub issue attached). */
  allowCreate?: boolean;
  /** The workspace root the last new folder went into, so the next one goes there too. */
  lastNewFolderRoot?: string | null;
  disabled?: boolean;
}>(), {
  allowCreate: false,
  lastNewFolderRoot: null,
  disabled: false,
});

const emit = defineEmits<{
  "update:folder": [folder: NewSessionFolder];
  closeAutoFocus: [event: Event];
  /** A folder was added to the workspace roots, so the repository list is out of date. */
  folderAdded: [];
  /** A folder was created or cloned inside this workspace root. */
  created: [root: string];
  /** A clone is under way for the folder the session will use; starting has to wait. */
  "update:busy": [busy: boolean];
}>();

const open = defineModel<boolean>("open", { default: false });

interface FolderOption {
  id: string;
  group: "Recent" | "Repositories" | "New" | "Other";
  title: string;
  /** Bold part of the title, after it: the name to create or the repository to clone. */
  titleName?: string;
  detail: string;
  mono: boolean;
  icon: "repository" | "directory" | "browse" | "none" | "create" | "clone" | "new";
  isSelected: boolean;
  choose: () => void;
}

type NewFolderKind = "empty" | "git" | "clone";
/** A workspace root, or "other" for a parent folder typed in. */
type NewFolderLocation = string;
const OTHER_LOCATION = "\u0000other";

const listId = useId();
const view = shallowRef<"list" | "browse" | "new">("list");
const query = shallowRef("");
const highlightedIndex = shallowRef(0);
const directoryDraft = shallowRef("");
const isDirectoryPickerOpen = shallowRef(false);
const searchInput = useTemplateRef<HTMLInputElement>("search");
const directoryInput = useTemplateRef<HTMLInputElement>("directory");
const newNameInput = useTemplateRef<HTMLInputElement>("newName");
const newRepositoryInput = useTemplateRef<HTMLInputElement>("newRepository");
// Any folder on this computer: one outside the workspace roots can be added from here.
const directoryBrowser = useDirectoryBrowser(false, { unconstrained: true });
const browseStatus = shallowRef<"idle" | "checking" | "adding">("idle");
const browseError = shallowRef<string | null>(null);
/** A folder that was picked but is outside the workspace roots, waiting to be added. */
const folderToAdd = shallowRef<FolderInspection | null>(null);
/** A typed path with no folder there yet, waiting to be created. */
const folderToCreate = shallowRef<FolderInspection | null>(null);
const startGitRepository = shallowRef(true);

// Making folders.
const roots = shallowRef<readonly string[] | null>(null);
const pickedRoot = shallowRef<string | null>(null);
const makeStatus = shallowRef<"idle" | "creating" | "cloning">("idle");
const makeError = shallowRef<string | null>(null);
/** The folder that was already there, so it can be used instead. */
const existingPath = shallowRef<string | null>(null);
const makeWarning = shallowRef<string | null>(null);
const cloneProgress = shallowRef<CloneProgress | null>(null);
const cloningName = shallowRef<string | null>(null);
/** Another folder was picked while a clone ran: the clone finishes without taking over. */
const isCloneSuperseded = shallowRef(false);
/** The menu is being reopened to show how a clone ended, so opening mustn't reset it. */
let keepStateOnOpen = false;
/** Folders made from this menu, which the chip marks as new. */
const createdPaths = shallowRef<ReadonlySet<string>>(new Set());
const newKind = shallowRef<NewFolderKind>("git");
const newName = shallowRef("");
const newRepository = shallowRef("");
const newLocation = shallowRef<NewFolderLocation>("");
const newParent = shallowRef("");
const gitHubRepos = useGitHubRepos({ autoLoad: false });

function baseName(path: string): string {
  return path.split(/[/\\]/).filter(Boolean).pop() ?? path;
}

function isCurrent(candidate: NewSessionFolder): boolean {
  const current = props.folder;
  if (!current || current.kind !== candidate.kind) {
    return false;
  }
  return current.kind === "none" || (candidate.kind !== "none" && current.path === candidate.path);
}

function choose(folder: NewSessionFolder): void {
  if (makeStatus.value === "cloning" && !isCloneSuperseded.value) {
    isCloneSuperseded.value = true;
    emit("update:busy", false);
  }
  emit("update:folder", folder);
  open.value = false;
}

function repositoryOption(repository: ScannedRepository, group: FolderOption["group"]): FolderOption {
  const folder: NewSessionFolder = { kind: "repository", path: repository.path };
  return {
    id: `${group}:${repository.path}`,
    group,
    title: repository.name,
    detail: tildePath(repository.path),
    mono: true,
    icon: "repository",
    isSelected: isCurrent(folder),
    choose: () => choose(folder),
  };
}

// ── Where new folders go ──────────────────────────────────────────────────

const currentPath = computed(() => (props.folder && props.folder.kind !== "none" ? props.folder.path : null));
const newFolderRoot = computed(() =>
  pickedRoot.value ?? defaultNewFolderRoot(roots.value ?? [], props.lastNewFolderRoot, currentPath.value));

async function loadRoots(): Promise<void> {
  if (!props.allowCreate) {
    return;
  }
  try {
    roots.value = await listWorkspaceRoots();
  } catch {
    roots.value = [];
  }
}

/** What the search box asks for: a repository to clone, or the name of a folder to create. */
const cloneFromQuery = computed<CloneSource | null>(() => (props.allowCreate ? parseCloneSource(query.value) : null));
const nameFromQuery = computed(() => {
  if (!props.allowCreate || cloneFromQuery.value) {
    return "";
  }
  const name = folderNameFrom(query.value);
  const isTaken = props.repositories.some((repository) => repository.name.toLowerCase() === name.toLowerCase());
  return isTaken ? "" : name;
});

const options = computed<FolderOption[]>(() => {
  const needle = query.value.trim().toLowerCase();
  // A pasted repository address never matches a local repository by text.
  const matches = (text: string) => !needle || (!cloneFromQuery.value && text.toLowerCase().includes(needle));
  const byPath = new Map(props.repositories.map((repository) => [repository.path, repository]));

  const recent: FolderOption[] = [];
  const recentPaths = new Set<string>();
  for (const folder of props.recentFolders) {
    if (folder.kind === "repository") {
      const repository = byPath.get(folder.path);
      if (repository && matches(`${repository.name} ${repository.path}`)) {
        recent.push(repositoryOption(repository, "Recent"));
      }
      recentPaths.add(folder.path);
    } else if (folder.kind === "directory" && props.allowBrowse && matches(folder.path)) {
      recent.push({
        id: `Recent:dir:${folder.path}`,
        group: "Recent",
        title: baseName(folder.path),
        detail: tildePath(folder.path),
        mono: true,
        icon: "directory",
        isSelected: isCurrent(folder),
        choose: () => choose(folder),
      });
    }
  }

  const others = [...props.repositories]
    .filter((repository) => !recentPaths.has(repository.path) && matches(`${repository.name} ${repository.path}`))
    .sort((left, right) => left.name.localeCompare(right.name))
    .map((repository) => repositoryOption(repository, "Repositories"));

  const made: FolderOption[] = [];
  const root = newFolderRoot.value;
  if (root && cloneFromQuery.value) {
    const source = cloneFromQuery.value;
    const target = joinPath(root, source.name);
    made.push({
      id: "clone",
      group: "New",
      title: "Clone",
      titleName: source.label,
      detail: `into ${tildePath(target)}`,
      mono: false,
      icon: "clone",
      isSelected: false,
      choose: () => void clone(source, target, root),
    });
  } else if (root && nameFromQuery.value) {
    const target = joinPath(root, nameFromQuery.value);
    made.push({
      id: "create",
      group: "New",
      title: "Create",
      titleName: nameFromQuery.value,
      detail: `${tildePath(target)} · git repository`,
      mono: false,
      icon: "create",
      isSelected: false,
      choose: () => void create(target, true, root),
    });
  }

  const extras: FolderOption[] = [];
  if (props.allowCreate) {
    extras.push({
      id: "new",
      group: "Other",
      title: "New folder…",
      detail: "Empty, a git repository, or a clone",
      mono: false,
      icon: "new",
      isSelected: false,
      choose: () => showNew(),
    });
  }
  if (props.allowBrowse) {
    extras.push({
      id: "browse",
      group: "Other",
      title: "Browse for a folder…",
      detail: "Any repository or folder on this computer",
      mono: false,
      icon: "browse",
      isSelected: props.folder?.kind === "directory",
      choose: () => showBrowse(),
    });
  }
  if (props.allowNone) {
    extras.push({
      id: "none",
      group: "Other",
      title: "No folder, just chat",
      detail: "Ask anything without a repository",
      mono: false,
      icon: "none",
      isSelected: props.folder?.kind === "none",
      choose: () => choose({ kind: "none" }),
    });
  }

  return [...recent, ...others, ...made, ...extras];
});

const hasRepositoryMatches = computed(() => options.value.some((option) => option.icon === "repository"));
const offersToMake = computed(() => options.value.some((option) => option.group === "New"));

/** The row's title; a create or clone row says what it's doing while it runs. */
function optionTitle(option: FolderOption): string {
  if (option.group === "New" && makeStatus.value === "creating") {
    return "Creating";
  }
  if (option.group === "New" && makeStatus.value === "cloning") {
    return "Cloning";
  }
  return option.title;
}

function optionDomId(index: number): string {
  return `${listId}-option-${index}`;
}

function groupLabel(index: number): string | null {
  const option = options.value[index];
  if (!option || (index > 0 && options.value[index - 1]?.group === option.group)) {
    return null;
  }
  if (option.group === "New" && option.icon === "clone") {
    return "Repository to clone";
  }
  if (option.group === "New") {
    return hasRepositoryMatches.value ? "Or start something new" : "No repository matches";
  }
  return option.group === "Other" ? null : option.group;
}

function startsOther(index: number): boolean {
  return index > 0 && options.value[index]?.group === "Other" && options.value[index - 1]?.group !== "Other";
}

function handleSearchKeydown(event: KeyboardEvent): void {
  const count = options.value.length;
  if (count === 0) {
    return;
  }

  if (event.key === "ArrowDown") {
    event.preventDefault();
    highlightedIndex.value = (highlightedIndex.value + 1) % count;
    scrollHighlightedIntoView();
  } else if (event.key === "ArrowUp") {
    event.preventDefault();
    highlightedIndex.value = (highlightedIndex.value - 1 + count) % count;
    scrollHighlightedIntoView();
  } else if (event.key === "Enter") {
    event.preventDefault();
    const option = options.value[highlightedIndex.value];
    if (option) {
      chooseOption(option);
    }
  }
}

/** A create or clone row does nothing while one is already running; every other row still works. */
function chooseOption(option: FolderOption): void {
  if (option.group !== "New" || makeStatus.value === "idle") {
    option.choose();
  }
}

function scrollHighlightedIntoView(): void {
  void nextTick(() => {
    document.getElementById(optionDomId(highlightedIndex.value))?.scrollIntoView?.({ block: "nearest" });
  });
}

// ── Making a folder ───────────────────────────────────────────────────────

function resetMake(): void {
  makeError.value = null;
  existingPath.value = null;
  makeWarning.value = null;
}

function failed(error: unknown, fallback: string): void {
  makeError.value = error instanceof Error ? error.message : fallback;
  existingPath.value = error instanceof FolderExistsError ? error.path : null;
}

/** Uses a folder that was just made; a warning keeps the menu open so it can be read. */
function useMade(made: NewFolder, root: string | null): void {
  createdPaths.value = new Set([...createdPaths.value, made.path]);
  emit("folderAdded");
  if (root) {
    emit("created", root);
  }
  const folder: NewSessionFolder = made.isGitRepo
    ? { kind: "repository", path: made.path }
    : { kind: "directory", path: made.path };
  if (made.warning) {
    emit("update:folder", folder);
    makeWarning.value = made.warning;
    return;
  }
  choose(folder);
}

async function create(path: string, git: boolean, root: string | null): Promise<void> {
  if (makeStatus.value !== "idle") {
    return;
  }
  resetMake();
  makeStatus.value = "creating";
  try {
    useMade(await createFolder(path, git), root);
  } catch (error) {
    failed(error, "Couldn't create that folder.");
  } finally {
    makeStatus.value = "idle";
  }
}

async function clone(source: CloneSource, path: string, root: string | null): Promise<void> {
  if (makeStatus.value !== "idle") {
    return;
  }
  resetMake();
  makeStatus.value = "cloning";
  cloneProgress.value = null;
  cloningName.value = source.name;
  isCloneSuperseded.value = false;
  emit("update:busy", true);
  try {
    const made = await cloneRepository(source.repository, path, (progress) => {
      cloneProgress.value = progress;
    });
    if (isCloneSuperseded.value) {
      createdPaths.value = new Set([...createdPaths.value, made.path]);
      emit("folderAdded");
    } else {
      useMade(made, root);
    }
  } catch (error) {
    failed(error, "Couldn't clone that repository.");
    if (!isCloneSuperseded.value && !open.value) {
      keepStateOnOpen = true;
      open.value = true;
    }
  } finally {
    makeStatus.value = "idle";
    cloningName.value = null;
    if (!isCloneSuperseded.value) {
      emit("update:busy", false);
    }
  }
}

async function useExisting(): Promise<void> {
  const path = existingPath.value;
  if (!path) {
    return;
  }
  resetMake();
  try {
    const inspection = await inspectFolder(path);
    if (inspection.isWithinRoots) {
      choose(folderForInspection(inspection));
    } else {
      directoryDraft.value = inspection.path;
      folderToAdd.value = inspection;
      view.value = "browse";
    }
  } catch (error) {
    failed(error, "Couldn't check that folder.");
  }
}

const cloneStatusText = computed(() => {
  const progress = cloneProgress.value;
  return progress ? `${progress.phase} ${progress.percent}%` : "Starting…";
});

// ── New folder form ───────────────────────────────────────────────────────

function showNew(): void {
  resetMake();
  const source = cloneFromQuery.value;
  newKind.value = source ? "clone" : "git";
  newRepository.value = source ? query.value.trim() : "";
  newName.value = source ? "" : folderNameFrom(query.value);
  newLocation.value = newFolderRoot.value ?? OTHER_LOCATION;
  newParent.value = "";
  view.value = "new";
  focusNewForm();
}

function focusNewForm(): void {
  void nextTick(() => (newKind.value === "clone" ? newRepositoryInput.value : newNameInput.value)?.focus());
}

function setNewKind(kind: NewFolderKind): void {
  newKind.value = kind;
  resetMake();
  if (kind === "clone") {
    void gitHubRepos.refresh();
  }
  focusNewForm();
}

const newSource = computed(() => (newKind.value === "clone" ? parseCloneSource(newRepository.value) : null));
const newFolderName = computed(() => (newKind.value === "clone" ? newSource.value?.name ?? "" : folderNameFrom(newName.value)));
const newParentPath = computed(() => (newLocation.value === OTHER_LOCATION ? newParent.value.trim() : newLocation.value));
const newTarget = computed(() =>
  newFolderName.value && newParentPath.value ? joinPath(newParentPath.value, newFolderName.value) : null);
const isNewOutsideRoots = computed(() => newLocation.value === OTHER_LOCATION);

const newDescription = computed(() => {
  if (makeStatus.value === "cloning") {
    return cloneStatusText.value;
  }
  const what = {
    empty: "An empty folder",
    git: "A git repository with an empty first commit",
    clone: newSource.value ? `A clone of ${newSource.value.label}` : "A clone",
  }[newKind.value];
  return isNewOutsideRoots.value ? `${what}, added to your folders` : what;
});

/** Your GitHub repositories that aren't on this computer, for Clone. */
const cloneSuggestions = computed(() => {
  if (newKind.value !== "clone" || newSource.value) {
    return [];
  }
  const local = new Set(props.repositories.map((repository) => repository.name.toLowerCase()));
  const needle = newRepository.value.trim().toLowerCase();
  return gitHubRepos.repos.value
    .filter((repo) => !local.has(repo.name.toLowerCase()) && (!needle || repo.full_name.toLowerCase().includes(needle)))
    .slice(0, 5);
});

const canSubmitNew = computed(() =>
  makeStatus.value === "idle" && newTarget.value !== null && (newKind.value !== "clone" || newSource.value !== null));

function submitNew(): void {
  const target = newTarget.value;
  if (!canSubmitNew.value || !target) {
    return;
  }
  const root = isNewOutsideRoots.value ? null : newLocation.value;
  if (newKind.value === "clone" && newSource.value) {
    void clone(newSource.value, target, root);
  } else {
    void create(target, newKind.value === "git", root);
  }
}

// ── Browse ────────────────────────────────────────────────────────────────

function resetBrowseCheck(): void {
  browseError.value = null;
  folderToAdd.value = null;
  folderToCreate.value = null;
  resetMake();
}

function showBrowse(): void {
  directoryDraft.value = props.folder?.kind === "directory" ? props.folder.path : "";
  resetBrowseCheck();
  view.value = "browse";
  void nextTick(() => directoryInput.value?.focus());
}

function showList(): void {
  resetMake();
  view.value = "list";
  void nextTick(() => searchInput.value?.focus());
}

/**
 * Uses the typed or picked folder: a git checkout becomes a repository (so it can get a
 * worktree), anything else a plain folder. One outside the workspace roots asks to be added first;
 * one that isn't there yet offers to be created.
 */
async function useDirectory(path = directoryDraft.value): Promise<void> {
  const trimmed = path.trim();
  if (!trimmed || browseStatus.value !== "idle") {
    return;
  }

  directoryDraft.value = trimmed;
  resetBrowseCheck();
  browseStatus.value = "checking";
  try {
    const inspection = await inspectFolder(trimmed);
    if (!inspection.exists && props.allowCreate) {
      folderToCreate.value = inspection;
      startGitRepository.value = true;
    } else if (!inspection.exists) {
      browseError.value = "There's no folder at that path.";
    } else if (!inspection.isWithinRoots) {
      directoryDraft.value = inspection.path;
      folderToAdd.value = inspection;
    } else {
      choose(folderForInspection(inspection));
    }
  } catch (error) {
    browseError.value = error instanceof Error ? error.message : "Couldn't check that folder.";
  } finally {
    browseStatus.value = "idle";
  }
}

async function addAndUseFolder(): Promise<void> {
  const inspection = folderToAdd.value;
  if (!inspection || browseStatus.value !== "idle") {
    return;
  }

  browseStatus.value = "adding";
  try {
    await addFolderToFleet(inspection.path);
    emit("folderAdded");
    choose(folderForInspection(inspection));
  } catch (error) {
    folderToAdd.value = null;
    browseError.value = error instanceof Error ? error.message : "Couldn't add that folder.";
  } finally {
    browseStatus.value = "idle";
  }
}

const createRoot = computed(() => (folderToCreate.value ? rootContaining(folderToCreate.value.path, roots.value ?? []) : null));

function createTypedFolder(): void {
  const inspection = folderToCreate.value;
  if (inspection) {
    void create(inspection.path, startGitRepository.value, createRoot.value);
  }
}

function handleDirectoryEnter(): void {
  if (folderToAdd.value) {
    void addAndUseFolder();
  } else if (folderToCreate.value) {
    createTypedFolder();
  } else {
    void useDirectory();
  }
}

function handleDirectoryPickerOpenChange(value: boolean): void {
  if (value) {
    directoryBrowser.browse(directoryDraft.value.trim() || null);
  }
  isDirectoryPickerOpen.value = value;
}

watch(query, () => {
  highlightedIndex.value = 0;
  if (view.value === "list") {
    resetMake();
  }
});

watch(open, (isOpen) => {
  if (isOpen) {
    if (makeStatus.value === "cloning" || keepStateOnOpen) {
      keepStateOnOpen = false;
      return;
    }
    query.value = "";
    view.value = "list";
    pickedRoot.value = null;
    resetMake();
    void loadRoots();
    // After the query watcher, which would put the highlight back on the first row.
    void nextTick(() => {
      const selected = options.value.findIndex((option) => option.isSelected);
      highlightedIndex.value = selected >= 0 ? selected : 0;
      scrollHighlightedIntoView();
    });
  } else {
    isDirectoryPickerOpen.value = false;
  }
});

const chipLabel = computed(() => {
  if (makeStatus.value === "cloning" && !isCloneSuperseded.value) {
    return `Cloning ${cloningName.value}… ${cloneProgress.value ? `${cloneProgress.value.percent}%` : ""}`.trim();
  }
  const folder = props.folder;
  if (!folder) {
    return "Choose a folder";
  }
  if (folder.kind === "none") {
    return "No folder";
  }
  if (folder.kind === "directory") {
    return tildePath(folder.path);
  }
  return props.repositories.find((repository) => repository.path === folder.path)?.name ?? baseName(folder.path);
});

const isChipCloning = computed(() => makeStatus.value === "cloning" && !isCloneSuperseded.value);
const isNew = computed(() => currentPath.value !== null && createdPaths.value.has(currentPath.value));
</script>

<template>
  <Popover v-model:open="open">
    <PopoverTrigger as-child>
      <button
        type="button"
        class="ns-chip"
        :class="{ 'ns-chip--attention': !folder && !isChipCloning }"
        data-testid="new-session-folder-chip"
        :disabled="disabled"
        :title="folder && folder.kind !== 'none' ? folder.path : undefined"
      >
        <LoaderCircle
          v-if="isChipCloning"
          class="ns-chip__icon animate-spin"
          aria-hidden="true"
        />
        <MessageSquare
          v-else-if="folder?.kind === 'none'"
          class="ns-chip__icon"
          aria-hidden="true"
        />
        <Folder
          v-else-if="folder?.kind === 'directory'"
          class="ns-chip__icon"
          aria-hidden="true"
        />
        <FolderGit2
          v-else
          class="ns-chip__icon"
          aria-hidden="true"
        />
        <span class="ns-chip__label">{{ chipLabel }}</span>
        <span
          v-if="isNew && !isChipCloning"
          class="ns-folder-new-tag"
          data-testid="new-session-folder-new"
        >new</span>
        <ChevronDown
          class="ns-chip__chevron"
          aria-hidden="true"
        />
      </button>
    </PopoverTrigger>

    <PopoverContent
      class="ns-pop"
      side="top"
      align="start"
      :side-offset="6"
      :collision-padding="8"
      @close-auto-focus="emit('closeAutoFocus', $event)"
    >
      <div
        v-if="makeWarning"
        class="ns-folder-add ns-folder-warning"
        role="status"
        data-testid="new-session-folder-warning"
      >
        <GitBranch
          class="ns-folder-add__icon"
          aria-hidden="true"
        />
        <div class="ns-folder-add__text">
          <p class="ns-folder-add__title">
            Created {{ currentPath ? tildePath(currentPath) : "the folder" }}
          </p>
          <p class="ns-folder-add__detail">
            {{ makeWarning }}
          </p>
        </div>
        <div class="ns-folder-actions ns-folder-warning__actions">
          <Button
            type="button"
            size="sm"
            @click="open = false"
          >
            OK
          </Button>
        </div>
      </div>

      <template v-else-if="view === 'list'">
        <div class="ns-folder-search">
          <Search
            class="ns-folder-search__icon"
            aria-hidden="true"
          />
          <input
            ref="search"
            v-model="query"
            type="text"
            class="ns-folder-search__input"
            :placeholder="allowCreate ? 'Search, name a new folder, or paste a repository…' : 'Search repositories…'"
            autocomplete="off"
            spellcheck="false"
            role="combobox"
            :aria-label="allowCreate ? 'Search, or create a folder' : 'Search repositories'"
            aria-expanded="true"
            :aria-controls="listId"
            :aria-activedescendant="options.length > 0 ? optionDomId(highlightedIndex) : undefined"
            @keydown="handleSearchKeydown"
          >
        </div>

        <div
          :id="listId"
          class="ns-folder-list"
          role="listbox"
          aria-label="Folders"
        >
          <template
            v-for="(option, index) in options"
            :key="option.id"
          >
            <div
              v-if="startsOther(index)"
              class="ns-pop__separator"
              role="presentation"
            />
            <div
              v-else-if="groupLabel(index)"
              class="ns-pop__label"
              role="presentation"
            >
              {{ groupLabel(index) }}
            </div>
            <button
              :id="optionDomId(index)"
              type="button"
              class="ns-option"
              :class="{ 'ns-option--make': option.group === 'New' }"
              role="option"
              tabindex="-1"
              :aria-selected="option.isSelected"
              :data-highlighted="index === highlightedIndex ? '' : undefined"
              :data-testid="option.group === 'New' ? `new-session-folder-${option.id}` : undefined"
              @click="chooseOption(option)"
              @mousemove="highlightedIndex = index"
            >
              <LoaderCircle
                v-if="option.group === 'New' && makeStatus !== 'idle'"
                class="ns-option__icon animate-spin"
                aria-hidden="true"
              />
              <FolderGit2
                v-else-if="option.icon === 'repository'"
                class="ns-option__icon"
                aria-hidden="true"
              />
              <Folder
                v-else-if="option.icon === 'directory'"
                class="ns-option__icon"
                aria-hidden="true"
              />
              <FolderOpen
                v-else-if="option.icon === 'browse'"
                class="ns-option__icon"
                aria-hidden="true"
              />
              <FolderPlus
                v-else-if="option.icon === 'create'"
                class="ns-option__icon"
                aria-hidden="true"
              />
              <Github
                v-else-if="option.icon === 'clone'"
                class="ns-option__icon"
                aria-hidden="true"
              />
              <Plus
                v-else-if="option.icon === 'new'"
                class="ns-option__icon"
                aria-hidden="true"
              />
              <MessageSquare
                v-else
                class="ns-option__icon"
                aria-hidden="true"
              />
              <span class="ns-option__text">
                <span class="ns-option__title">
                  {{ optionTitle(option) }}
                  <strong v-if="option.titleName">{{ option.titleName }}</strong>
                </span>
                <span
                  class="ns-option__detail"
                  :class="{ 'ns-option__detail--mono': option.mono }"
                >{{ option.group === 'New' && makeStatus === 'cloning' ? cloneStatusText : option.detail }}</span>
              </span>
              <Check
                v-if="option.isSelected"
                class="ns-option__check"
                aria-hidden="true"
              />
            </button>
          </template>

          <p
            v-if="!hasRepositoryMatches && !offersToMake"
            class="ns-pop__note"
          >
            <template v-if="query.trim()">
              No repository matches “{{ query.trim() }}”.
            </template>
            <template v-else-if="allowCreate">
              No repositories yet. Type a name to create one.
            </template>
            <template v-else-if="allowBrowse">
              No repositories yet. Browse for a folder to add one.
            </template>
            <template v-else>
              No repositories found in your workspace roots.
            </template>
          </p>
        </div>

        <div
          v-if="offersToMake && (roots?.length ?? 0) > 1"
          class="ns-folder-root"
        >
          <label
            :for="`${listId}-root`"
            class="ns-folder-root__label"
          >New folders go in</label>
          <select
            :id="`${listId}-root`"
            class="ns-folder-root__select"
            data-testid="new-session-folder-root"
            :value="newFolderRoot ?? ''"
            @change="pickedRoot = ($event.target as HTMLSelectElement).value"
          >
            <option
              v-for="root in roots"
              :key="root"
              :value="root"
            >
              {{ tildePath(root) }}
            </option>
          </select>
        </div>

        <p
          v-if="makeError"
          class="ns-folder-error"
          role="alert"
        >
          {{ makeError }}
          <button
            v-if="existingPath"
            type="button"
            class="ns-folder-link"
            @click="useExisting"
          >
            Use that folder
          </button>
        </p>
      </template>

      <template v-else-if="view === 'new'">
        <button
          type="button"
          class="ns-option ns-folder-back"
          @click="showList"
        >
          <ArrowLeft
            class="ns-option__icon"
            aria-hidden="true"
          />
          <span class="ns-option__text">
            <span class="ns-option__title">All folders</span>
          </span>
        </button>
        <div class="ns-pop__separator" />
        <div
          class="ns-field"
          data-testid="new-session-new-folder"
        >
          <span
            :id="`${listId}-kind`"
            class="ns-field__label"
          >Start with</span>
          <div
            class="ns-folder-kinds"
            role="group"
            :aria-labelledby="`${listId}-kind`"
          >
            <button
              type="button"
              :aria-pressed="newKind === 'empty'"
              :disabled="makeStatus !== 'idle'"
              @click="setNewKind('empty')"
            >
              <Folder aria-hidden="true" />Empty folder
            </button>
            <button
              type="button"
              :aria-pressed="newKind === 'git'"
              :disabled="makeStatus !== 'idle'"
              @click="setNewKind('git')"
            >
              <GitBranch aria-hidden="true" />Git repository
            </button>
            <button
              type="button"
              :aria-pressed="newKind === 'clone'"
              :disabled="makeStatus !== 'idle'"
              @click="setNewKind('clone')"
            >
              <Github aria-hidden="true" />Clone
            </button>
          </div>
        </div>
        <div
          v-if="newKind !== 'clone'"
          class="ns-field"
        >
          <label
            :for="`${listId}-name`"
            class="ns-field__label"
          >Name</label>
          <input
            :id="`${listId}-name`"
            ref="newName"
            v-model="newName"
            type="text"
            class="ns-field__input ns-field__input--mono"
            placeholder="my-project"
            autocomplete="off"
            spellcheck="false"
            data-testid="new-session-new-folder-name"
            @input="resetMake"
            @keydown.enter.prevent="submitNew"
          >
        </div>
        <template v-else>
          <div class="ns-field">
            <label
              :for="`${listId}-repository`"
              class="ns-field__label"
            >Repository</label>
            <input
              :id="`${listId}-repository`"
              ref="newRepository"
              v-model="newRepository"
              type="text"
              class="ns-field__input ns-field__input--mono"
              placeholder="owner/repo, or an https or ssh address"
              autocomplete="off"
              spellcheck="false"
              data-testid="new-session-new-folder-repository"
              :readonly="makeStatus !== 'idle'"
              @input="resetMake"
              @keydown.enter.prevent="submitNew"
            >
          </div>
          <div
            v-if="cloneSuggestions.length > 0"
            class="ns-folder-suggestions"
          >
            <button
              v-for="repo in cloneSuggestions"
              :key="repo.id"
              type="button"
              class="ns-folder-suggestion"
              @click="newRepository = repo.full_name"
            >
              <Github aria-hidden="true" />
              <span>{{ repo.full_name }}</span>
            </button>
          </div>
        </template>
        <div class="ns-field">
          <label
            :for="`${listId}-location`"
            class="ns-field__label"
          >Location</label>
          <select
            :id="`${listId}-location`"
            v-model="newLocation"
            class="ns-field__input"
            data-testid="new-session-new-folder-location"
            :disabled="makeStatus !== 'idle'"
          >
            <option
              v-for="root in roots ?? []"
              :key="root"
              :value="root"
            >
              {{ tildePath(root) }}
            </option>
            <option :value="OTHER_LOCATION">
              Another folder…
            </option>
          </select>
          <input
            v-if="newLocation === OTHER_LOCATION"
            v-model="newParent"
            type="text"
            class="ns-field__input ns-field__input--mono"
            placeholder="/path/to/parent"
            aria-label="Parent folder"
            autocomplete="off"
            spellcheck="false"
            data-testid="new-session-new-folder-parent"
            @input="resetMake"
            @keydown.enter.prevent="submitNew"
          >
        </div>
        <div
          v-if="newTarget"
          class="ns-folder-preview"
          data-testid="new-session-new-folder-preview"
        >
          <span class="ns-folder-preview__path">{{ tildePath(newTarget) }}</span>
          <span class="ns-folder-preview__what">{{ newDescription }}</span>
        </div>
        <p
          v-if="makeError"
          class="ns-folder-error"
          role="alert"
        >
          {{ makeError }}
          <button
            v-if="existingPath"
            type="button"
            class="ns-folder-link"
            @click="useExisting"
          >
            Use that folder
          </button>
        </p>
        <div class="ns-folder-actions">
          <Button
            type="button"
            size="sm"
            variant="ghost"
            :disabled="makeStatus !== 'idle'"
            @click="showList"
          >
            Cancel
          </Button>
          <Button
            type="button"
            size="sm"
            data-testid="new-session-new-folder-submit"
            :disabled="!canSubmitNew"
            @click="submitNew"
          >
            <template v-if="makeStatus === 'cloning'">
              Cloning…
            </template>
            <template v-else-if="makeStatus === 'creating'">
              Creating…
            </template>
            <template v-else>
              {{ newKind === "clone" ? "Clone and use" : "Create and use" }}
            </template>
          </Button>
        </div>
      </template>

      <template v-else>
        <button
          type="button"
          class="ns-option ns-folder-back"
          @click="showList"
        >
          <ArrowLeft
            class="ns-option__icon"
            aria-hidden="true"
          />
          <span class="ns-option__text">
            <span class="ns-option__title">All folders</span>
          </span>
        </button>
        <div class="ns-pop__separator" />
        <div class="ns-field">
          <label
            for="new-session-directory"
            class="ns-field__label"
          >Folder</label>
          <div class="ns-folder-path">
            <input
              id="new-session-directory"
              ref="directory"
              v-model="directoryDraft"
              type="text"
              class="ns-field__input ns-field__input--mono"
              placeholder="/path/to/folder"
              autocomplete="off"
              spellcheck="false"
              @input="resetBrowseCheck"
              @keydown.enter.prevent="handleDirectoryEnter"
            >
            <DirectoryPickerPopover
              :browser="directoryBrowser"
              :open="isDirectoryPickerOpen"
              mode="navigate"
              align="end"
              content-class="w-[25rem] max-w-[calc(100vw-2rem)]"
              @update:open="handleDirectoryPickerOpenChange"
              @select="useDirectory"
            >
              <template #trigger>
                <Button
                  type="button"
                  variant="outline"
                  size="icon"
                  class="shrink-0"
                  aria-label="Browse folders"
                  title="Browse folders"
                >
                  <FolderOpen class="h-4 w-4" />
                </Button>
              </template>
            </DirectoryPickerPopover>
          </div>
        </div>
        <p
          v-if="browseError || makeError"
          class="ns-folder-error"
          role="alert"
        >
          {{ browseError ?? makeError }}
        </p>
        <div
          v-if="folderToCreate"
          class="ns-folder-add"
          role="status"
          data-testid="new-session-create-folder"
        >
          <FolderPlus
            class="ns-folder-add__icon"
            aria-hidden="true"
          />
          <div class="ns-folder-add__text">
            <p class="ns-folder-add__title">
              {{ baseName(folderToCreate.path) }} doesn't exist yet
            </p>
            <p class="ns-folder-add__detail">
              Fleet can create {{ tildePath(folderToCreate.path) }}{{ roots && !createRoot ? " and add it to your folders" : "" }}.
            </p>
            <label class="ns-folder-check">
              <input
                v-model="startGitRepository"
                type="checkbox"
                data-testid="new-session-create-folder-git"
              >
              Start a git repository
            </label>
          </div>
        </div>
        <div
          v-else-if="folderToAdd"
          class="ns-folder-add"
          role="status"
          data-testid="new-session-add-folder"
        >
          <FolderPlus
            class="ns-folder-add__icon"
            aria-hidden="true"
          />
          <div class="ns-folder-add__text">
            <p class="ns-folder-add__title">
              {{ baseName(folderToAdd.path) }} isn't in Fleet yet
            </p>
            <p class="ns-folder-add__detail">
              {{ folderToAdd.isGitRepo
                ? "Add this repository to work in its checkout or in new worktrees beside it."
                : "Add this folder so sessions can work in it." }}
              You can remove it in Settings.
            </p>
          </div>
        </div>
        <div class="ns-folder-actions">
          <template v-if="folderToCreate">
            <Button
              type="button"
              size="sm"
              variant="ghost"
              :disabled="makeStatus !== 'idle'"
              @click="resetBrowseCheck"
            >
              Cancel
            </Button>
            <Button
              type="button"
              size="sm"
              data-testid="new-session-create-folder-submit"
              :disabled="makeStatus !== 'idle'"
              @click="createTypedFolder"
            >
              {{ makeStatus === "creating" ? "Creating…" : "Create folder" }}
            </Button>
          </template>
          <template v-else-if="folderToAdd">
            <Button
              type="button"
              size="sm"
              variant="ghost"
              :disabled="browseStatus !== 'idle'"
              @click="resetBrowseCheck"
            >
              Cancel
            </Button>
            <Button
              type="button"
              size="sm"
              :disabled="browseStatus !== 'idle'"
              @click="addAndUseFolder()"
            >
              {{ browseStatus === "adding" ? "Adding…" : folderToAdd.isGitRepo ? "Add repository" : "Add folder" }}
            </Button>
          </template>
          <Button
            v-else
            type="button"
            size="sm"
            :disabled="!directoryDraft.trim() || browseStatus !== 'idle'"
            @click="useDirectory()"
          >
            {{ browseStatus === "checking" ? "Checking…" : "Use this folder" }}
          </Button>
        </div>
      </template>
    </PopoverContent>
  </Popover>
</template>

<style scoped>
.ns-folder-search {
  display: flex;
  align-items: center;
  gap: 7px;
  margin: -1px -1px 4px;
  padding: 5px 8px 7px;
  border-bottom: 1px solid var(--border);
  color: var(--muted);
}

.ns-folder-search__icon {
  width: 14px;
  height: 14px;
  flex-shrink: 0;
}

.ns-folder-search__input {
  min-width: 0;
  flex: 1;
  border: 0;
  background: transparent;
  color: var(--text);
  font-size: 13px;
  outline: none;
}

.ns-folder-search__input::placeholder {
  color: var(--muted);
}

.ns-folder-list {
  max-height: min(340px, 50vh);
  overflow-y: auto;
  overscroll-behavior: contain;
}

.ns-option--make .ns-option__icon {
  color: var(--accent);
}

.ns-option--make strong {
  font-weight: 600;
}

.ns-folder-back {
  grid-template-columns: 16px minmax(0, 1fr);
}

.ns-folder-path {
  display: flex;
  gap: 6px;
}

.ns-folder-actions {
  display: flex;
  justify-content: flex-end;
  gap: 6px;
  padding: 0 8px 6px;
}

.ns-folder-error {
  margin: 0 8px 8px;
  color: var(--error);
  font-size: 12.5px;
}

.ns-folder-link {
  border: 0;
  background: none;
  padding: 0;
  color: var(--accent);
  cursor: pointer;
  font: inherit;
}

.ns-folder-link:hover {
  text-decoration: underline;
}

.ns-folder-add {
  display: grid;
  grid-template-columns: 16px minmax(0, 1fr);
  gap: 8px;
  margin: 0 8px 8px;
  padding: 8px;
  border: 1px solid color-mix(in srgb, var(--accent) 35%, var(--border));
  border-radius: calc(var(--radius-btn) - 2px);
  background: color-mix(in srgb, var(--accent) 7%, transparent);
}

.ns-folder-add__icon {
  width: 14px;
  height: 14px;
  margin-top: 2px;
  color: var(--accent);
}

.ns-folder-add__text {
  display: grid;
  min-width: 0;
  gap: 2px;
}

.ns-folder-add__title {
  overflow: hidden;
  font-weight: 500;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.ns-folder-add__detail {
  color: var(--muted);
  font-size: 12px;
  line-height: 1.4;
  overflow-wrap: anywhere;
}

.ns-folder-warning {
  margin: 3px;
}

.ns-folder-warning__actions {
  grid-column: 1 / -1;
  padding: 2px 0 0;
}

.ns-folder-check {
  display: flex;
  align-items: center;
  gap: 7px;
  margin-top: 6px;
  cursor: pointer;
  font-size: 12.5px;
}

.ns-folder-check input {
  margin: 0;
  accent-color: var(--accent);
}

.ns-folder-root {
  display: flex;
  align-items: center;
  gap: 6px;
  margin-top: 4px;
  padding: 6px 8px 2px;
  border-top: 1px solid var(--border);
  color: var(--muted);
  font-size: 12px;
}

.ns-folder-root__select {
  min-width: 0;
  flex: 1;
  padding: 2px 4px;
  border: 1px solid var(--border);
  border-radius: calc(var(--radius-btn) - 3px);
  background: transparent;
  color: var(--text);
  font-family: var(--font-mono-stack);
  font-size: 11.5px;
}

.ns-folder-kinds {
  display: grid;
  grid-template-columns: repeat(3, minmax(0, 1fr));
  gap: 2px;
  padding: 2px;
  border: 1px solid var(--border);
  border-radius: calc(var(--radius-btn) - 2px);
}

.ns-folder-kinds button {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  gap: 5px;
  padding: 5px 4px;
  border: 0;
  border-radius: calc(var(--radius-btn) - 4px);
  background: transparent;
  color: var(--muted);
  cursor: pointer;
  font-size: 12px;
  white-space: nowrap;
}

.ns-folder-kinds button svg {
  width: 13px;
  height: 13px;
  flex-shrink: 0;
}

.ns-folder-kinds button[aria-pressed="true"] {
  background: color-mix(in srgb, var(--text) 8%, transparent);
  color: var(--text);
  font-weight: 500;
}

.ns-folder-suggestions {
  display: grid;
  gap: 1px;
  padding: 0 8px 6px;
}

.ns-folder-suggestion {
  display: flex;
  align-items: center;
  gap: 6px;
  padding: 4px 6px;
  border: 0;
  border-radius: calc(var(--radius-btn) - 4px);
  background: transparent;
  color: var(--text);
  cursor: pointer;
  font-family: var(--font-mono-stack);
  font-size: 11.5px;
  text-align: left;
}

.ns-folder-suggestion:hover {
  background: color-mix(in srgb, var(--text) 6%, transparent);
}

.ns-folder-suggestion svg {
  width: 12px;
  height: 12px;
  flex-shrink: 0;
  color: var(--muted);
}

.ns-folder-preview {
  display: grid;
  gap: 2px;
  margin: 0 8px 8px;
  padding: 7px 8px;
  border-radius: calc(var(--radius-btn) - 2px);
  background: color-mix(in srgb, var(--text) 4%, transparent);
}

.ns-folder-preview__path {
  font-family: var(--font-mono-stack);
  font-size: 12px;
  overflow-wrap: anywhere;
}

.ns-folder-preview__what {
  color: var(--muted);
  font-size: 11.5px;
}

.ns-folder-new-tag {
  padding: 0 4px;
  border-radius: 4px;
  background: color-mix(in srgb, var(--running) 14%, transparent);
  color: var(--running);
  font-size: 10px;
  font-weight: 600;
  line-height: 16px;
}
</style>
