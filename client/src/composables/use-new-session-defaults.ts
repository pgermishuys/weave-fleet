import type { ScannedRepository, WorktreeInfo } from "@/api/client";
import { usePersistedState } from "@/composables/use-persisted-state";
import { DEFAULT_CHOICE, type AgentModelChoice } from "@/lib/agent-model-choice";
import type { NewSessionFolder, NewSessionWorkspace } from "@/lib/new-session-request";

/** `"current"`, `"new"`, or the path of an existing worktree. */
type RememberedWorkspace = string;

interface NewSessionDefaults {
  lastFolder: NewSessionFolder | null;
  /** Repositories and folders, most recent first. */
  recentFolders: NewSessionFolder[];
  workspaceByRepository: Record<string, RememberedWorkspace>;
  /** The agent and model last started with, by folder (path, or "none" for a quick chat) and then harness. */
  choiceByFolder: Record<string, Record<string, AgentModelChoice>>;
  /** The workspace root the last new folder or clone went into. */
  lastNewFolderRoot: string | null;
}

export const NEW_SESSION_DEFAULTS_KEY = "weave:new-session:defaults";
const MAX_RECENT_FOLDERS = 5;

const EMPTY_DEFAULTS: NewSessionDefaults = {
  lastFolder: null,
  recentFolders: [],
  workspaceByRepository: {},
  choiceByFolder: {},
  lastNewFolderRoot: null,
};

export interface UseNewSessionDefaultsResult {
  /** The folder to start with: the last one used, if it still exists; null the first time. */
  initialFolder: (repositories: readonly ScannedRepository[]) => NewSessionFolder | null;
  /** Recently used repositories and folders, without repositories that are gone. */
  recentFolders: (repositories: readonly ScannedRepository[]) => NewSessionFolder[];
  /**
   * The workspace last used in a repository; New worktree the first time. A remembered worktree
   * that isn't in `worktrees` falls back to New worktree; pass null while worktrees are loading.
   */
  workspaceFor: (repositoryPath: string, worktrees: readonly WorktreeInfo[] | null) => NewSessionWorkspace;
  /** The worktree path last used in a repository, if any, so menus can list it first. */
  lastWorktreeFor: (repositoryPath: string) => string | null;
  /** The agent and model last started with in a folder on a harness; Default for both the first time. */
  choiceFor: (folder: NewSessionFolder, harnessType: string) => AgentModelChoice;
  /** The workspace root the last new folder or clone went into, if any. */
  lastNewFolderRoot: () => string | null;
  /** Records where a new folder or clone went, so the next one goes there too. */
  rememberNewFolderRoot: (root: string) => void;
  /** Records the choices a session was just created with. */
  remember: (folder: NewSessionFolder, workspace: NewSessionWorkspace, choice?: { harnessType: string } & AgentModelChoice) => void;
}

function sameFolder(left: NewSessionFolder, right: NewSessionFolder): boolean {
  if (left.kind === "none" || right.kind === "none") {
    return left.kind === right.kind;
  }
  return left.kind === right.kind && left.path === right.path;
}

function folderKey(folder: NewSessionFolder): string {
  return folder.kind === "none" ? "none" : folder.path;
}

function isChoice(value: unknown): value is AgentModelChoice {
  if (typeof value !== "object" || value === null) {
    return false;
  }
  const choice = value as Partial<Record<keyof AgentModelChoice, unknown>>;
  return typeof choice.agent === "string" && typeof choice.model === "string";
}

function normalizeChoices(value: unknown): Record<string, Record<string, AgentModelChoice>> {
  if (typeof value !== "object" || value === null) {
    return {};
  }
  return Object.fromEntries(Object.entries(value).flatMap(([folder, byHarness]) => {
    if (typeof byHarness !== "object" || byHarness === null) {
      return [];
    }
    return [[folder, Object.fromEntries(Object.entries(byHarness).filter(([, choice]) => isChoice(choice)))]];
  })) as Record<string, Record<string, AgentModelChoice>>;
}

function isFolder(value: unknown): value is NewSessionFolder {
  if (typeof value !== "object" || value === null) {
    return false;
  }
  const folder = value as Partial<{ kind: unknown; path: unknown }>;
  return folder.kind === "none"
    || ((folder.kind === "repository" || folder.kind === "directory") && typeof folder.path === "string" && folder.path.length > 0);
}

/** Whatever is in storage, made safe to read: older or hand-edited values never throw. */
function normalize(stored: unknown): NewSessionDefaults {
  if (typeof stored !== "object" || stored === null) {
    return EMPTY_DEFAULTS;
  }
  const value = stored as Partial<Record<keyof NewSessionDefaults, unknown>>;
  const workspaces = typeof value.workspaceByRepository === "object" && value.workspaceByRepository !== null
    ? Object.fromEntries(Object.entries(value.workspaceByRepository).filter(([, workspace]) => typeof workspace === "string"))
    : {};

  return {
    lastFolder: isFolder(value.lastFolder) ? value.lastFolder : null,
    recentFolders: Array.isArray(value.recentFolders) ? value.recentFolders.filter(isFolder) : [],
    workspaceByRepository: workspaces as Record<string, string>,
    choiceByFolder: normalizeChoices(value.choiceByFolder),
    lastNewFolderRoot: typeof value.lastNewFolderRoot === "string" ? value.lastNewFolderRoot : null,
  };
}

function stillExists(folder: NewSessionFolder, repositories: readonly ScannedRepository[]): boolean {
  return folder.kind !== "repository" || repositories.some((repository) => repository.path === folder.path);
}

function toRemembered(workspace: NewSessionWorkspace): RememberedWorkspace {
  return workspace.kind === "existing" ? workspace.path : workspace.kind;
}

export function useNewSessionDefaults(): UseNewSessionDefaultsResult {
  const [stored, setStored] = usePersistedState<NewSessionDefaults>(NEW_SESSION_DEFAULTS_KEY, EMPTY_DEFAULTS);

  function initialFolder(repositories: readonly ScannedRepository[]): NewSessionFolder | null {
    const { lastFolder, recentFolders } = normalize(stored.value);
    if (lastFolder && stillExists(lastFolder, repositories)) {
      return lastFolder;
    }
    // The last repository is gone: the most recent one that's still there, else nothing.
    return recentFolders.find((folder) => folder.kind === "repository" && stillExists(folder, repositories)) ?? null;
  }

  function recentFolders(repositories: readonly ScannedRepository[]): NewSessionFolder[] {
    return normalize(stored.value).recentFolders.filter((folder) => stillExists(folder, repositories));
  }

  function workspaceFor(repositoryPath: string, worktrees: readonly WorktreeInfo[] | null): NewSessionWorkspace {
    const remembered = normalize(stored.value).workspaceByRepository[repositoryPath];
    if (remembered === "current") {
      return { kind: "current" };
    }
    if (!remembered || remembered === "new") {
      return { kind: "new" };
    }
    if (worktrees !== null && !worktrees.some((worktree) => worktree.path === remembered)) {
      return { kind: "new" };
    }
    return { kind: "existing", path: remembered };
  }

  function lastWorktreeFor(repositoryPath: string): string | null {
    const remembered = normalize(stored.value).workspaceByRepository[repositoryPath];
    return remembered && remembered !== "current" && remembered !== "new" ? remembered : null;
  }

  function choiceFor(folder: NewSessionFolder, harnessType: string): AgentModelChoice {
    return normalize(stored.value).choiceByFolder[folderKey(folder)]?.[harnessType] ?? DEFAULT_CHOICE;
  }

  function lastNewFolderRoot(): string | null {
    return normalize(stored.value).lastNewFolderRoot;
  }

  function rememberNewFolderRoot(root: string): void {
    setStored((previous) => ({ ...normalize(previous), lastNewFolderRoot: root }));
  }

  function remember(
    folder: NewSessionFolder,
    workspace: NewSessionWorkspace,
    choice?: { harnessType: string } & AgentModelChoice,
  ): void {
    setStored((previous) => {
      const current = normalize(previous);
      const key = folderKey(folder);
      const choices = choice
        ? {
          ...current.choiceByFolder,
          [key]: { ...current.choiceByFolder[key], [choice.harnessType]: { agent: choice.agent, model: choice.model } },
        }
        : current.choiceByFolder;
      const recent = folder.kind === "none"
        ? current.recentFolders
        : [folder, ...current.recentFolders.filter((entry) => !sameFolder(entry, folder))].slice(0, MAX_RECENT_FOLDERS);

      return {
        lastFolder: folder,
        recentFolders: recent,
        workspaceByRepository: folder.kind === "repository"
          ? { ...current.workspaceByRepository, [folder.path]: toRemembered(workspace) }
          : current.workspaceByRepository,
        choiceByFolder: choices,
        lastNewFolderRoot: current.lastNewFolderRoot,
      };
    });
  }

  return {
    initialFolder,
    recentFolders,
    workspaceFor,
    lastWorktreeFor,
    choiceFor,
    lastNewFolderRoot,
    rememberNewFolderRoot,
    remember,
  };
}
