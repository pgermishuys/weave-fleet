<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, shallowRef, useId, useTemplateRef, watch } from "vue";
import {
  ArrowLeft,
  Check,
  ChevronDown,
  CornerLeftUp,
  Folder,
  FolderGit2,
  FolderOpen,
  FolderPlus,
  GitBranch,
  Github,
  Info,
  LoaderCircle,
  MapPin,
  MessageSquare,
  Search,
} from "lucide-vue-next";
import type { ScannedRepository } from "@/api/client";
import { Button } from "@/components/ui/button";
import { Popover, PopoverContent, PopoverTrigger } from "@/components/ui/popover";
import { useMachineTarget } from "@/lib/machine-target";
import {
  addFolderToFleet,
  cloneRepository,
  createFolder,
  FolderExistsError,
  folderForInspection,
  inspectFolder,
  listFolder,
  listWorkspaceRoots,
  newFolderDefaults,
  type CloneProgress,
  type FolderListing,
  type NewFolder,
} from "@/lib/folder-access";
import {
  defaultNewFolderRoot,
  folderPathFrom,
  foldersBetween,
  invalidNameCharacter,
  isRootedPath,
  joinPath,
  parseCloneSource,
  rootContaining,
  separatorOf,
  splitTypedPath,
  withSeparator,
  type CloneSource,
} from "@/lib/new-folder";
import { tildePath } from "@/lib/new-session-plan";
import type { NewSessionFolder } from "@/lib/new-session-request";
import { useGitHubRepos } from "@/plugins/builtin/github/composables/use-github-repos";

const props = withDefaults(defineProps<{
  folder: NewSessionFolder | null;
  repositories: readonly ScannedRepository[];
  recentFolders: readonly NewSessionFolder[];
  /** Plain folders sessions ran in, which the repository scan never lists. */
  plainFolders?: readonly string[];
  /** Any folder on disk (not in cloud mode, not with a GitHub issue attached). */
  allowBrowse: boolean;
  /** No folder at all (not with a GitHub issue attached). */
  allowNone: boolean;
  /** Create a folder or clone a repository from the menu (not in cloud mode, not with a GitHub issue attached). */
  allowCreate?: boolean;
  /** The location the last new folder went into, so the next one goes there too. */
  lastNewFolderRoot?: string | null;
  disabled?: boolean;
}>(), {
  plainFolders: () => [],
  allowCreate: false,
  lastNewFolderRoot: null,
  disabled: false,
});

const emit = defineEmits<{
  "update:folder": [folder: NewSessionFolder];
  closeAutoFocus: [event: Event];
  /** A folder was added to the locations, so the repository list is out of date. */
  folderAdded: [];
  /** A folder was created or cloned inside this location. */
  created: [root: string];
  /** A clone is under way for the folder the session will use; starting has to wait. */
  "update:busy": [busy: boolean];
}>();

const open = defineModel<boolean>("open", { default: false });

interface FolderOption {
  id: string;
  group: "Recent" | "Repositories" | "Folders" | "New" | "Other";
  title: string;
  /** Bold part of the title, after it: the name to create or the repository to clone. */
  titleName?: string;
  detail: string;
  mono: boolean;
  icon: "repository" | "directory" | "browse" | "none" | "create" | "clone";
  isSelected: boolean;
  choose: () => void;
}

/** A row in the folder box: open a folder, go into one, or create the one typed. */
interface PathRow {
  id: string;
  action: "open" | "enter" | "create";
  title: string;
  titleName?: string;
  detail: string;
  path: string;
  isGitRepo: boolean;
}

/** A location, or "other" for a parent folder typed in (Clone only). */
type CloneLocation = string;
const OTHER_LOCATION = `${String.fromCharCode(0)}other`;

const listId = useId();
const view = shallowRef<"list" | "path" | "clone">("list");
const query = shallowRef("");
const highlightedIndex = shallowRef(0);
const searchInput = useTemplateRef<HTMLInputElement>("search");
const pathInput = useTemplateRef<HTMLInputElement>("path");
const cloneRepositoryInput = useTemplateRef<HTMLInputElement>("cloneRepository");
// Folders on the machine the session starts on.
const { api: machineApi } = useMachineTarget();
const openStatus = shallowRef<"idle" | "checking" | "adding">("idle");

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
/** The first branch a new repository gets on this machine unless told otherwise. */
const firstBranch = shallowRef<string | null>(null);
/** The machine's home folder, which the folder box writes as `~`; null until known. */
const home = shallowRef<string | null>(null);
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

function directoryOption(path: string, group: FolderOption["group"]): FolderOption {
  const folder: NewSessionFolder = { kind: "directory", path };
  return {
    id: `${group}:dir:${path}`,
    group,
    title: baseName(path),
    detail: tildePath(path),
    mono: true,
    icon: "directory",
    isSelected: isCurrent(folder),
    choose: () => choose(folder),
  };
}

// ── Where new folders go ──────────────────────────────────────────────────

const currentPath = computed(() => (props.folder && props.folder.kind !== "none" ? props.folder.path : null));
const newFolderRoot = computed(() =>
  pickedRoot.value ?? defaultNewFolderRoot(roots.value ?? [], props.lastNewFolderRoot, currentPath.value));
/** How this machine writes paths, judged from one of its folders. */
const separator = computed(() =>
  separatorOf(roots.value?.[0] ?? currentPath.value ?? props.repositories[0]?.path ?? null));

async function loadRoots(): Promise<void> {
  if (!props.allowCreate && !props.allowBrowse) {
    return;
  }
  try {
    roots.value = await listWorkspaceRoots(machineApi);
  } catch {
    roots.value = [];
  }
}

async function loadDefaults(): Promise<void> {
  if ((!props.allowCreate && !props.allowBrowse) || firstBranch.value !== null) {
    return;
  }
  try {
    const defaults = await newFolderDefaults(machineApi);
    firstBranch.value = defaults.firstBranch;
    home.value = defaults.home;
  } catch {
    firstBranch.value = "main";
  }
  if (!branch.value) {
    branch.value = firstBranch.value;
  }
}

/** What the search box asks for: a repository to clone, or a folder (or folders) to create. */
const cloneFromQuery = computed<CloneSource | null>(() => (props.allowCreate ? parseCloneSource(query.value) : null));
/** A path of its own (`~/…`, `/…`, `C:\…`) typed in the search box: one for the folder box, not the location. */
const isQueryRooted = computed(() => props.allowBrowse && !cloneFromQuery.value && isRootedPath(query.value.trim()));
const typedFromQuery = computed(() =>
  props.allowCreate && !cloneFromQuery.value && !isQueryRooted.value && query.value.trim() ? folderPathFrom(query.value) : null);
/** The folder the search box would create inside the location, if it can. */
const createFromQuery = computed<string | null>(() => {
  const typed = typedFromQuery.value;
  const root = newFolderRoot.value;
  if (!typed || typed.invalid || typed.segments.length === 0 || !root) {
    return null;
  }
  const [only] = typed.segments;
  const isTaken = typed.segments.length === 1
    && props.repositories.some((repository) => repository.name.toLowerCase() === only!.toLowerCase());
  return isTaken ? null : typed.segments.reduce((path, segment) => joinPath(path, segment), root);
});

function repositoryLabel(): string {
  return firstBranch.value ? `git repository on ${firstBranch.value}` : "git repository";
}

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
    } else if (folder.kind === "directory" && props.allowBrowse) {
      if (matches(folder.path)) {
        recent.push(directoryOption(folder.path, "Recent"));
      }
      recentPaths.add(folder.path);
    }
  }

  const others = [...props.repositories]
    .filter((repository) => !recentPaths.has(repository.path) && matches(`${repository.name} ${repository.path}`))
    .sort((left, right) => left.name.localeCompare(right.name))
    .map((repository) => repositoryOption(repository, "Repositories"));

  const plain = props.allowBrowse
    ? props.plainFolders
      .filter((path) => !recentPaths.has(path) && matches(path))
      .map((path) => directoryOption(path, "Folders"))
    : [];

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
  } else if (root && createFromQuery.value) {
    const target = createFromQuery.value;
    made.push({
      id: "create",
      group: "New",
      title: "Create",
      titleName: foldersBetween(root, target).join(separatorOf(root)),
      detail: `${tildePath(target)} · ${repositoryLabel()}`,
      mono: false,
      icon: "create",
      isSelected: false,
      choose: () => void create(target, true, root),
    });
  } else if (isQueryRooted.value) {
    const typed = query.value.trim();
    made.push({
      id: "go",
      group: "New",
      title: "Go to",
      titleName: typed,
      detail: props.allowCreate ? "Open it, or create it if it isn't there" : "Open it",
      mono: false,
      icon: "browse",
      isSelected: false,
      choose: () => showPath(typed),
    });
  }

  const extras: FolderOption[] = [];
  if (props.allowBrowse) {
    extras.push({
      id: "path",
      group: "Other",
      title: props.allowCreate ? "Open or create a folder…" : "Open a folder…",
      detail: props.allowCreate ? "Type a path; Fleet lists what's there" : "Any repository or folder on this computer",
      mono: false,
      icon: "browse",
      isSelected: false,
      choose: () => showPath(),
    });
  }
  if (props.allowCreate) {
    extras.push({
      id: "clone-form",
      group: "Other",
      title: "Clone a repository…",
      detail: "From GitHub or any git address",
      mono: false,
      icon: "clone",
      isSelected: false,
      choose: () => showClone(),
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

  return [...recent, ...others, ...plain, ...made, ...extras];
});

const hasFolderMatches = computed(() => options.value.some((option) => option.icon === "repository" || option.icon === "directory"));
const offersToMake = computed(() => options.value.some((option) => option.id === "create" || option.id === "clone"));
/** A character typed in the search box that no folder name can have. */
const invalidInQuery = computed(() => typedFromQuery.value?.invalid ?? null);

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
    return hasFolderMatches.value ? "Or start something new" : "No folder matches";
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
    scrollIntoView(optionDomId(highlightedIndex.value));
  } else if (event.key === "ArrowUp") {
    event.preventDefault();
    highlightedIndex.value = (highlightedIndex.value - 1 + count) % count;
    scrollIntoView(optionDomId(highlightedIndex.value));
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

function scrollIntoView(id: string): void {
  void nextTick(() => {
    document.getElementById(id)?.scrollIntoView?.({ block: "nearest" });
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

async function create(path: string, git: boolean, root: string | null, firstBranchName?: string): Promise<void> {
  if (makeStatus.value !== "idle") {
    return;
  }
  resetMake();
  makeStatus.value = "creating";
  try {
    useMade(await createFolder(path, git, machineApi, git ? firstBranchName : undefined), root);
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
    }, machineApi);
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

/**
 * Uses a folder that's there: a git checkout becomes a repository (so it can get a worktree), anything
 * else a plain folder. One outside the locations is added to them first, so it's listed next time.
 */
async function openFolder(path: string): Promise<void> {
  if (openStatus.value !== "idle") {
    return;
  }
  resetMake();
  openStatus.value = "checking";
  try {
    const inspection = await inspectFolder(path, machineApi);
    if (!inspection.exists) {
      makeError.value = "There's no folder at that path.";
      return;
    }
    if (!inspection.isWithinRoots) {
      openStatus.value = "adding";
      await addFolderToFleet(inspection.path, machineApi);
      emit("folderAdded");
    }
    choose(folderForInspection(inspection));
  } catch (error) {
    failed(error, "Couldn't open that folder.");
  } finally {
    openStatus.value = "idle";
  }
}

function useExisting(): void {
  const path = existingPath.value;
  if (path) {
    void openFolder(path);
  }
}

const cloneStatusText = computed(() => {
  const progress = cloneProgress.value;
  return progress ? `${progress.phase} ${progress.percent}%` : "Starting…";
});

// ── The folder box: open or create a folder ──────────────────────────────

const pathText = shallowRef("");
/** The last listing, and the folder it was asked for: rows only use it while it matches what's typed. */
const listing = shallowRef<{ request: string; result: FolderListing } | null>(null);
const listingError = shallowRef<string | null>(null);
const pathHighlight = shallowRef(0);
const newKind = shallowRef<"empty" | "git">("git");
const branch = shallowRef("");
let listingRequest: AbortController | null = null;

function withoutTrailingSeparator(path: string): string {
  // Keep a root's own separator: `/`, `C:\`.
  return /^([/\\]|[A-Za-z]:[/\\])$/.test(path) ? path : path.replace(/[/\\]+$/, "");
}

const typedPath = computed(() => splitTypedPath(pathText.value));
/** The folder whose contents the box lists: what's typed up to the last separator, inside the location when it isn't a path of its own. */
const folderToList = computed(() => {
  const { folder } = typedPath.value;
  const location = newFolderRoot.value ?? "~";
  if (!folder) {
    return location;
  }
  const typed = withSeparator(folder, separator.value);
  return withoutTrailingSeparator(isRootedPath(typed) ? typed : joinPath(location, typed));
});
const currentListing = computed(() => (listing.value?.request === folderToList.value ? listing.value.result : null));

async function loadListing(path: string): Promise<void> {
  listingRequest?.abort();
  const request = new AbortController();
  listingRequest = request;
  try {
    const result = await listFolder(path, machineApi, request.signal);
    if (!request.signal.aborted) {
      listing.value = { request: path, result };
      listingError.value = null;
    }
  } catch (error) {
    if (!request.signal.aborted) {
      listingError.value = error instanceof Error ? error.message : "Couldn't list that folder.";
    }
  }
}

watch([folderToList, view], ([path, currentView]) => {
  if (currentView === "path") {
    void loadListing(path);
  }
});

onBeforeUnmount(() => listingRequest?.abort());

/** The new folders a create would make, and the folder that's there above them. */
const pathCreate = computed<{ base: string; target: string; folders: string[]; invalid: string | null } | null>(() => {
  const result = currentListing.value;
  if (!result || !props.allowCreate) {
    return null;
  }
  const name = typedPath.value.name.trim();
  const base = result.exists ? result.path : result.nearestExisting;
  const isThere = result.entries.some((entry) => entry.name.toLowerCase() === name.toLowerCase());
  if (!base || (result.exists && (!name || isThere))) {
    return null;
  }
  const target = name ? joinPath(result.path, name) : result.path;
  const folders = foldersBetween(base, target);
  const invalid = folders.map(invalidNameCharacter).find((character) => character !== null) ?? null;
  return { base, target, folders, invalid };
});

const pathRows = computed<PathRow[]>(() => {
  const result = currentListing.value;
  if (!result) {
    return [];
  }
  const name = typedPath.value.name.trim();
  const create = pathCreate.value && !pathCreate.value.invalid
    ? {
      id: "create",
      action: "create" as const,
      title: "Create",
      titleName: pathCreate.value.folders.join(separatorOf(result.path)),
      detail: `in ${shownPath(pathCreate.value.base)}`,
      path: pathCreate.value.target,
      isGitRepo: false,
    }
    : null;
  if (!result.exists) {
    return create ? [create] : [];
  }

  const needle = name.toLowerCase();
  const matches = result.entries.filter((entry) => entry.name.toLowerCase().startsWith(needle));
  const exact = result.entries.find((entry) => entry.name === name)
    ?? result.entries.find((entry) => entry.name.toLowerCase() === needle && needle !== "");
  const rows: PathRow[] = [];
  if (!name) {
    rows.push({ id: "open-here", action: "open", title: "Open", titleName: baseName(result.path), detail: shownPath(result.path), path: result.path, isGitRepo: false });
  } else if (exact) {
    rows.push({ id: "open-exact", action: "open", title: "Open", titleName: exact.name, detail: shownPath(exact.path), path: exact.path, isGitRepo: exact.isGitRepo });
  }
  // Create leads only when nothing starts with what was typed: otherwise Enter would make "cl" instead of opening "clients".
  if (create && matches.length === 0) {
    rows.push(create);
  }
  for (const entry of matches) {
    if (entry !== exact) {
      rows.push({
        id: `entry:${entry.path}`,
        action: "enter",
        title: entry.name,
        detail: entry.isGitRepo ? "Repository" : "Folder",
        path: entry.path,
        isGitRepo: entry.isGitRepo,
      });
    }
  }
  if (create && matches.length > 0) {
    rows.push(create);
  }
  return rows;
});

const highlightedPathRow = computed(() => pathRows.value[pathHighlight.value] ?? null);
/** Where the highlighted row would add a folder to the locations, for the line that says so. */
const addsLocation = computed(() => {
  const row = highlightedPathRow.value;
  return row !== null && row.action !== "enter" && roots.value !== null && rootContaining(row.path, roots.value) === null;
});
const pathNote = computed(() => {
  const result = currentListing.value;
  if (listingError.value) {
    return listingError.value;
  }
  if (!result) {
    return null;
  }
  if (pathCreate.value?.invalid) {
    return `“${pathCreate.value.invalid}” can't be in a folder name.`;
  }
  if (!result.exists && !props.allowCreate) {
    return `There's no folder at ${shownPath(result.path)}.`;
  }
  return null;
});

function pathRowDomId(index: number): string {
  return `${listId}-path-${index}`;
}

/**
 * A folder as the box writes it: `~` for the machine's own home folder, which the server expands the same
 * way. Anything else stays whole, because `~` there would mean a different folder.
 */
function typedPathFor(path: string): string {
  const own = home.value;
  if (!own || !path.toLowerCase().startsWith(own.toLowerCase())) {
    return path;
  }
  const rest = path.slice(own.length);
  return rest === "" || /^[/\\]/.test(rest) ? `~${rest}` : path;
}

/** A folder for the box's rows: the way the box writes it once the home folder is known. */
function shownPath(path: string): string {
  return home.value ? typedPathFor(path) : tildePath(path);
}

function showPath(text?: string): void {
  resetMake();
  const location = newFolderRoot.value;
  const sep = separator.value;
  pathText.value = text ?? (location ? `${typedPathFor(location)}${sep}` : `~${sep}`);
  pathHighlight.value = 0;
  newKind.value = "git";
  branch.value = firstBranch.value ?? "";
  view.value = "path";
  void nextTick(() => {
    const input = pathInput.value;
    input?.focus();
    input?.setSelectionRange(input.value.length, input.value.length);
  });
}

/** Goes into a folder: the box shows what's in it. */
function enterFolder(path: string): void {
  pathText.value = `${typedPathFor(path)}${separatorOf(path)}`;
  pathHighlight.value = 0;
  void nextTick(() => pathInput.value?.focus());
}

function goUp(): void {
  const here = currentListing.value?.path ?? folderToList.value;
  const index = Math.max(here.lastIndexOf("/"), here.lastIndexOf("\\"));
  if (index < 0) {
    return;
  }
  const parent = index === 0 ? here.slice(0, 1) : here.slice(0, index);
  enterFolder(/^[A-Za-z]:$/.test(parent) ? `${parent}\\` : parent);
}

function createFromPath(): void {
  const plan = pathCreate.value;
  if (!plan || plan.invalid) {
    return;
  }
  void create(plan.target, newKind.value === "git", rootContaining(plan.target, roots.value ?? []), branch.value.trim() || undefined);
}

function runPathRow(row: PathRow): void {
  if (row.action === "create") {
    createFromPath();
  } else if (row.action === "open" || row.isGitRepo) {
    void openFolder(row.path);
  } else {
    enterFolder(row.path);
  }
}

function handlePathKeydown(event: KeyboardEvent): void {
  const count = pathRows.value.length;
  if (event.key === "ArrowDown" && count > 0) {
    event.preventDefault();
    pathHighlight.value = (pathHighlight.value + 1) % count;
    scrollIntoView(pathRowDomId(pathHighlight.value));
  } else if (event.key === "ArrowUp" && count > 0) {
    event.preventDefault();
    pathHighlight.value = (pathHighlight.value - 1 + count) % count;
    scrollIntoView(pathRowDomId(pathHighlight.value));
  } else if (event.key === "Enter") {
    event.preventDefault();
    const row = highlightedPathRow.value;
    if (row && makeStatus.value === "idle") {
      runPathRow(row);
    }
  } else if (event.key === "Tab" && !event.shiftKey) {
    const row = highlightedPathRow.value;
    if (row?.action === "enter") {
      event.preventDefault();
      enterFolder(row.path);
    }
  }
}

watch(pathText, () => {
  pathHighlight.value = 0;
  if (view.value === "path") {
    resetMake();
  }
});

// ── Clone ─────────────────────────────────────────────────────────────────

const cloneText = shallowRef("");
const cloneLocation = shallowRef<CloneLocation>("");
const cloneParent = shallowRef("");

function showClone(): void {
  resetMake();
  cloneText.value = cloneFromQuery.value ? query.value.trim() : "";
  cloneLocation.value = newFolderRoot.value ?? OTHER_LOCATION;
  cloneParent.value = "";
  void gitHubRepos.refresh();
  view.value = "clone";
  void nextTick(() => cloneRepositoryInput.value?.focus());
}

const cloneSource = computed(() => parseCloneSource(cloneText.value));
const cloneParentPath = computed(() => (cloneLocation.value === OTHER_LOCATION ? cloneParent.value.trim() : cloneLocation.value));
const cloneTarget = computed(() =>
  cloneSource.value && cloneParentPath.value ? joinPath(cloneParentPath.value, cloneSource.value.name) : null);
const isCloneOutsideRoots = computed(() => cloneLocation.value === OTHER_LOCATION);

const cloneDescription = computed(() => {
  if (makeStatus.value === "cloning") {
    return cloneStatusText.value;
  }
  const what = cloneSource.value ? `A clone of ${cloneSource.value.label}` : "A clone";
  return isCloneOutsideRoots.value ? `${what}, added to your locations` : what;
});

/** Your GitHub repositories that aren't on this computer, for Clone. */
const cloneSuggestions = computed(() => {
  if (cloneSource.value) {
    return [];
  }
  const local = new Set(props.repositories.map((repository) => repository.name.toLowerCase()));
  const needle = cloneText.value.trim().toLowerCase();
  return gitHubRepos.repos.value
    .filter((repo) => !local.has(repo.name.toLowerCase()) && (!needle || repo.full_name.toLowerCase().includes(needle)))
    .slice(0, 5);
});

const canSubmitClone = computed(() => makeStatus.value === "idle" && cloneTarget.value !== null && cloneSource.value !== null);

function submitClone(): void {
  const target = cloneTarget.value;
  if (!canSubmitClone.value || !target || !cloneSource.value) {
    return;
  }
  void clone(cloneSource.value, target, isCloneOutsideRoots.value ? null : cloneLocation.value);
}

// ── Menu ──────────────────────────────────────────────────────────────────

function showList(): void {
  resetMake();
  view.value = "list";
  void nextTick(() => searchInput.value?.focus());
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
    void loadDefaults();
    // After the query watcher, which would put the highlight back on the first row.
    void nextTick(() => {
      const selected = options.value.findIndex((option) => option.isSelected);
      highlightedIndex.value = selected >= 0 ? selected : 0;
      scrollIntoView(optionDomId(highlightedIndex.value));
    });
  } else {
    listingRequest?.abort();
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
        <p
          v-if="invalidInQuery"
          class="ns-folder-error"
          role="alert"
          data-testid="new-session-folder-invalid"
        >
          “{{ invalidInQuery }}” can't be in a folder name.
        </p>

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
              :data-testid="option.group === 'New' || option.group === 'Other' ? `new-session-folder-${option.id}` : undefined"
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
            v-if="!hasFolderMatches && !offersToMake && !isQueryRooted && !invalidInQuery"
            class="ns-pop__note"
          >
            <template v-if="query.trim()">
              No folder matches “{{ query.trim() }}”.
            </template>
            <template v-else-if="allowCreate">
              No repositories yet. Type a name to create one.
            </template>
            <template v-else-if="allowBrowse">
              No repositories yet. Open a folder to add one.
            </template>
            <template v-else>
              No repositories found in your locations.
            </template>
          </p>
        </div>

        <div
          v-if="offersToMake && (roots?.length ?? 0) > 1"
          class="ns-folder-root"
        >
          <MapPin
            class="ns-folder-root__icon"
            aria-hidden="true"
          />
          <label
            :for="`${listId}-root`"
            class="ns-folder-root__label"
          >Location</label>
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

      <template v-else-if="view === 'path'">
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
          class="ns-field ns-folder-path-field"
          data-testid="new-session-path"
        >
          <label
            for="new-session-directory"
            class="ns-field__label"
          >{{ allowCreate ? "Open or create a folder" : "Open a folder" }}</label>
          <div class="ns-folder-pathbox">
            <FolderOpen
              class="ns-folder-pathbox__icon"
              aria-hidden="true"
            />
            <input
              id="new-session-directory"
              ref="path"
              v-model="pathText"
              type="text"
              class="ns-folder-pathbox__input"
              autocomplete="off"
              spellcheck="false"
              role="combobox"
              aria-expanded="true"
              :aria-controls="`${listId}-path`"
              :aria-activedescendant="highlightedPathRow ? pathRowDomId(pathHighlight) : undefined"
              data-testid="new-session-path-input"
              @keydown="handlePathKeydown"
            >
            <button
              type="button"
              class="ns-folder-pathbox__up"
              title="Up one folder"
              aria-label="Up one folder"
              @click="goUp"
            >
              <CornerLeftUp aria-hidden="true" />
            </button>
          </div>
        </div>

        <div
          :id="`${listId}-path`"
          class="ns-folder-list ns-folder-list--path"
          role="listbox"
          aria-label="What's in this folder"
        >
          <button
            v-for="(row, index) in pathRows"
            :id="pathRowDomId(index)"
            :key="row.id"
            type="button"
            class="ns-option ns-option--hint"
            :class="{ 'ns-option--make': row.action === 'create' }"
            role="option"
            tabindex="-1"
            :aria-selected="index === pathHighlight"
            :data-highlighted="index === pathHighlight ? '' : undefined"
            :data-testid="`new-session-path-${row.action}`"
            @click="runPathRow(row)"
            @mousemove="pathHighlight = index"
          >
            <LoaderCircle
              v-if="(row.action === 'create' && makeStatus === 'creating') || (row.action !== 'create' && openStatus !== 'idle' && index === pathHighlight)"
              class="ns-option__icon animate-spin"
              aria-hidden="true"
            />
            <FolderPlus
              v-else-if="row.action === 'create'"
              class="ns-option__icon"
              aria-hidden="true"
            />
            <FolderGit2
              v-else-if="row.isGitRepo"
              class="ns-option__icon"
              aria-hidden="true"
            />
            <FolderOpen
              v-else-if="row.action === 'open'"
              class="ns-option__icon"
              aria-hidden="true"
            />
            <Folder
              v-else
              class="ns-option__icon"
              aria-hidden="true"
            />
            <span class="ns-option__text">
              <span class="ns-option__title">
                {{ row.action === "create" && makeStatus === "creating" ? "Creating" : row.title }}
                <strong v-if="row.titleName">{{ row.titleName }}</strong>
              </span>
              <span
                class="ns-option__detail"
                :class="{ 'ns-option__detail--mono': row.action === 'open' }"
              >{{ row.detail }}</span>
            </span>
            <kbd
              v-if="index === pathHighlight"
              class="ns-folder-kbd"
            >{{ row.action === "enter" && !row.isGitRepo ? "Tab" : "Enter" }}</kbd>
            <span v-else />
          </button>
          <p
            v-if="!currentListing && !listingError"
            class="ns-pop__note"
          >
            Looking…
          </p>
          <p
            v-else-if="currentListing && pathRows.length === 0 && !pathNote"
            class="ns-pop__note"
          >
            Nothing in this folder.
          </p>
        </div>

        <p
          v-if="pathNote"
          class="ns-folder-error"
          role="alert"
          data-testid="new-session-path-note"
        >
          {{ pathNote }}
        </p>

        <template v-if="highlightedPathRow?.action === 'create' && pathCreate">
          <div class="ns-pop__separator" />
          <div
            class="ns-folder-create"
            data-testid="new-session-path-create-options"
          >
            <div
              class="ns-folder-kinds"
              role="group"
              aria-label="Start with"
            >
              <button
                type="button"
                :aria-pressed="newKind === 'empty'"
                :disabled="makeStatus !== 'idle'"
                data-testid="new-session-path-kind-empty"
                @click="newKind = 'empty'"
              >
                <Folder aria-hidden="true" />Empty folder
              </button>
              <button
                type="button"
                :aria-pressed="newKind === 'git'"
                :disabled="makeStatus !== 'idle'"
                data-testid="new-session-path-kind-git"
                @click="newKind = 'git'"
              >
                <GitBranch aria-hidden="true" />Git repository
              </button>
            </div>
            <label
              v-if="newKind === 'git'"
              class="ns-folder-branch"
            >
              on branch
              <input
                v-model="branch"
                type="text"
                class="ns-folder-branch__input"
                :placeholder="firstBranch ?? 'main'"
                autocomplete="off"
                spellcheck="false"
                data-testid="new-session-path-branch"
                @keydown.enter.prevent="createFromPath"
              >
            </label>
          </div>
          <div
            class="ns-folder-tree"
            data-testid="new-session-path-preview"
          >
            <div class="ns-folder-tree__row">
              <MapPin
                v-if="roots?.includes(pathCreate.base)"
                aria-hidden="true"
              />
              <Folder
                v-else
                aria-hidden="true"
              />
              {{ shownPath(pathCreate.base) }}
            </div>
            <div
              v-for="(name, depth) in pathCreate.folders"
              :key="depth"
              class="ns-folder-tree__row ns-folder-tree__row--new"
              :style="{ paddingLeft: `${(depth + 1) * 14}px` }"
            >
              <FolderPlus aria-hidden="true" />{{ name }}
              <span class="ns-folder-new-tag">new</span>
            </div>
            <p class="ns-folder-tree__what">
              <template v-if="newKind === 'git'">
                A git repository on <code>{{ branch.trim() || firstBranch || "main" }}</code> with an empty first commit
              </template>
              <template v-else>
                An empty folder
              </template>
            </p>
          </div>
        </template>

        <p
          v-if="addsLocation"
          class="ns-folder-hint"
          data-testid="new-session-path-adds-location"
        >
          <Info aria-hidden="true" />
          <span>Fleet adds {{ highlightedPathRow?.action === "create" ? "it" : highlightedPathRow?.titleName }} to your locations, so it's listed here next time.</span>
        </p>

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

        <div
          v-if="highlightedPathRow?.action === 'create' && pathCreate"
          class="ns-folder-actions"
        >
          <Button
            type="button"
            size="sm"
            data-testid="new-session-path-create-submit"
            :disabled="makeStatus !== 'idle'"
            @click="createFromPath"
          >
            {{ makeStatus === "creating" ? "Creating…" : "Create and use" }}
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
        <div
          class="ns-field"
          data-testid="new-session-clone"
        >
          <label
            :for="`${listId}-repository`"
            class="ns-field__label"
          >Clone a repository</label>
          <input
            :id="`${listId}-repository`"
            ref="cloneRepository"
            v-model="cloneText"
            type="text"
            class="ns-field__input ns-field__input--mono"
            placeholder="owner/repo, or an https or ssh address"
            autocomplete="off"
            spellcheck="false"
            data-testid="new-session-clone-repository"
            :readonly="makeStatus !== 'idle'"
            @input="resetMake"
            @keydown.enter.prevent="submitClone"
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
            @click="cloneText = repo.full_name"
          >
            <Github aria-hidden="true" />
            <span>{{ repo.full_name }}</span>
          </button>
        </div>
        <div class="ns-field">
          <label
            :for="`${listId}-location`"
            class="ns-field__label"
          >Location</label>
          <select
            :id="`${listId}-location`"
            v-model="cloneLocation"
            class="ns-field__input"
            data-testid="new-session-clone-location"
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
            v-if="cloneLocation === OTHER_LOCATION"
            v-model="cloneParent"
            type="text"
            class="ns-field__input ns-field__input--mono"
            placeholder="/path/to/parent"
            aria-label="Parent folder"
            autocomplete="off"
            spellcheck="false"
            data-testid="new-session-clone-parent"
            @input="resetMake"
            @keydown.enter.prevent="submitClone"
          >
        </div>
        <div
          v-if="cloneTarget"
          class="ns-folder-preview"
          data-testid="new-session-clone-preview"
        >
          <span class="ns-folder-preview__path">{{ tildePath(cloneTarget) }}</span>
          <span class="ns-folder-preview__what">{{ cloneDescription }}</span>
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
            data-testid="new-session-clone-submit"
            :disabled="!canSubmitClone"
            @click="submitClone"
          >
            {{ makeStatus === "cloning" ? "Cloning…" : "Clone and use" }}
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

.ns-folder-root__icon {
  width: 12px;
  height: 12px;
  flex-shrink: 0;
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
  grid-template-columns: repeat(2, minmax(0, 1fr));
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

/* The folder box: a path field over what's in the folder typed. */
.ns-folder-path-field {
  padding-bottom: 4px;
}

.ns-folder-pathbox {
  display: flex;
  align-items: center;
  gap: 6px;
  padding: 0 4px 0 8px;
  border: 1px solid var(--accent);
  border-radius: calc(var(--radius-btn) - 2px);
  background: color-mix(in srgb, var(--text) 3%, transparent);
}

.ns-folder-pathbox__icon {
  width: 14px;
  height: 14px;
  flex-shrink: 0;
  color: var(--muted);
}

.ns-folder-pathbox__input {
  min-width: 0;
  height: 30px;
  flex: 1;
  border: 0;
  background: transparent;
  color: var(--text);
  font-family: var(--font-mono-stack);
  font-size: 12px;
  outline: none;
}

.ns-folder-pathbox__up {
  display: inline-grid;
  width: 24px;
  height: 24px;
  flex-shrink: 0;
  place-items: center;
  border: 0;
  border-radius: calc(var(--radius-btn) - 4px);
  background: transparent;
  color: var(--muted);
  cursor: pointer;
}

.ns-folder-pathbox__up:hover {
  background: color-mix(in srgb, var(--text) 8%, transparent);
  color: var(--text);
}

.ns-folder-pathbox__up svg {
  width: 13px;
  height: 13px;
}

.ns-folder-list--path {
  max-height: min(240px, 36vh);
}

.ns-option--hint {
  grid-template-columns: 16px minmax(0, 1fr) auto;
}

.ns-folder-kbd {
  padding: 0 4px;
  border: 1px solid var(--border);
  border-radius: 4px;
  color: var(--muted);
  font-family: inherit;
  font-size: 10.5px;
  line-height: 16px;
}

.ns-folder-create {
  display: grid;
  gap: 8px;
  padding: 6px 8px 8px;
}

.ns-folder-branch {
  display: flex;
  align-items: center;
  gap: 6px;
  color: var(--muted);
  font-size: 12px;
}

.ns-folder-branch__input {
  width: 120px;
  height: 26px;
  padding: 0 7px;
  border: 1px solid var(--border);
  border-radius: calc(var(--radius-btn) - 3px);
  background: color-mix(in srgb, var(--text) 3%, transparent);
  color: var(--text);
  font-family: var(--font-mono-stack);
  font-size: 12px;
  outline: none;
}

.ns-folder-branch__input:focus {
  border-color: var(--accent);
}

.ns-folder-tree {
  display: grid;
  gap: 1px;
  margin: 0 8px 8px;
  padding: 7px 9px;
  border-radius: calc(var(--radius-btn) - 2px);
  background: color-mix(in srgb, var(--text) 4%, transparent);
  font-family: var(--font-mono-stack);
  font-size: 12px;
}

.ns-folder-tree__row {
  display: flex;
  min-width: 0;
  align-items: center;
  gap: 6px;
  overflow-wrap: anywhere;
}

.ns-folder-tree__row svg {
  width: 13px;
  height: 13px;
  flex-shrink: 0;
  color: var(--muted);
}

.ns-folder-tree__row--new svg {
  color: var(--accent);
}

.ns-folder-tree__what {
  margin-top: 4px;
  color: var(--muted);
  font-family: var(--font-sans-stack);
  font-size: 11.5px;
}

.ns-folder-tree__what code {
  font-family: var(--font-mono-stack);
  font-size: 11px;
}

.ns-folder-hint {
  display: flex;
  gap: 7px;
  margin: 0 8px 8px;
  color: var(--muted);
  font-size: 12px;
  line-height: 1.4;
}

.ns-folder-hint svg {
  width: 13px;
  height: 13px;
  flex-shrink: 0;
  margin-top: 2px;
  color: var(--status-waiting);
}
</style>
