import type { CreateSessionResponse, SessionListItem, SessionSourceSelection } from "@/api/client";
import type { CreateSessionOptions } from "@/composables/use-session-actions";
import {
  buildGitHubSessionSourceSelection,
  type GitHubSessionSourcePreset,
  type WorktreeBaseInput,
} from "@/lib/github-session-source";

/** Where the session runs: a scanned repository, any folder, or nowhere (a quick chat). */
export type NewSessionFolder =
  | { kind: "repository"; path: string }
  | { kind: "directory"; path: string }
  | { kind: "none" };

/** How a repository session gets its checkout. Only used when the folder is a repository. */
export type NewSessionWorkspace =
  | { kind: "current" }
  | { kind: "new" }
  | { kind: "existing"; path: string };

export interface NewSessionState {
  folder: NewSessionFolder;
  workspace: NewSessionWorkspace;
  message: string;
  title?: string;
  projectId?: string | null;
  tags?: readonly string[];
  harnessType?: string;
  /** The profile to start with, or `NO_PROFILE`; absent lets the server use the harness's default. */
  harnessProfileId?: string;
  gitHubPreset?: GitHubSessionSourcePreset | null;
  /** Branch for a new worktree, instead of the one generated from the message. */
  branch?: string;
  /** Where a new worktree starts (`origin/<name>` or a local branch); null or absent for the default. */
  baseBranch?: string | null;
  /** Fetch an `origin/…` base first (default true). */
  fetchOrigin?: boolean;
}

export interface NewSessionRequest {
  directory: string | undefined;
  options: CreateSessionOptions;
}

export type BuildNewSessionRequestResult =
  | ({ ok: true } & NewSessionRequest)
  | { ok: false; error: string };

export const BRANCH_PREFIX = "fleet/";
const MAX_SLUG_LENGTH = 40;
const MAX_TITLE_LENGTH = 60;

const STOP_WORDS = new Set([
  "a", "an", "the", "and", "or", "but", "so", "to", "of", "in", "on", "at", "for", "with", "from", "by",
  "into", "about", "as", "is", "are", "be", "it", "its", "this", "that", "these", "those",
  "i", "me", "my", "we", "us", "our", "you", "your", "please", "can", "could", "would", "should", "will",
  "lets", "let", "just", "some", "do", "does",
]);

function slugWords(text: string): string[] {
  return text
    .normalize("NFKD")
    .replace(/[\u0300-\u036f]/g, "")
    .toLowerCase()
    .replace(/['\u2019]/g, "")
    .split(/[^a-z0-9]+/)
    .filter((word) => word.length > 0);
}

/**
 * A branch-name slug for a message: its first line, lowercase words joined by hyphens,
 * filler words dropped, cut at a word boundary to 40 characters. Empty when nothing usable is left.
 */
export function slugForBranch(text: string): string {
  const firstLine = text.split(/\r?\n/).find((line) => line.trim().length > 0) ?? "";
  const words = slugWords(firstLine);
  const meaningful = words.filter((word) => !STOP_WORDS.has(word));
  const chosen = meaningful.length > 0 ? meaningful : words;

  let slug = "";
  for (const word of chosen) {
    const next = slug ? `${slug}-${word}` : word;
    if (next.length > MAX_SLUG_LENGTH) {
      // A single word longer than the limit is cut; otherwise stop at the last whole word.
      return slug || word.slice(0, MAX_SLUG_LENGTH);
    }
    slug = next;
  }

  return slug;
}

/**
 * A session title for a message: its first line, cut at a word boundary to 60 characters.
 * Fleet keeps the title it's given (the harness's own naming never reaches it), so this is
 * what the session is called until someone renames it.
 */
export function titleFromMessage(text: string): string {
  const firstLine = (text.split(/\r?\n/).find((line) => line.trim().length > 0) ?? "")
    .replace(/\s+/g, " ")
    .trim();
  if (firstLine.length <= MAX_TITLE_LENGTH) {
    return firstLine;
  }

  const cut = firstLine.slice(0, MAX_TITLE_LENGTH - 1);
  const lastSpace = cut.lastIndexOf(" ");
  return `${(lastSpace >= MAX_TITLE_LENGTH / 2 ? cut.slice(0, lastSpace) : cut).trimEnd()}…`;
}

/** `fleet/<slug>` for a message, or undefined when the message gives no slug (the server picks a name). */
export function branchForMessage(message: string): string | undefined {
  const slug = slugForBranch(message);
  return slug ? `${BRANCH_PREFIX}${slug}` : undefined;
}

/** The branch a new worktree will get: typed override, then the GitHub suggestion, then the message. */
export function resolveNewWorktreeBranch(state: Pick<NewSessionState, "branch" | "gitHubPreset" | "message">): string | undefined {
  return state.branch?.trim()
    || state.gitHubPreset?.suggestedBranch?.trim()
    || branchForMessage(state.message);
}

const QUICK_CHAT_SOURCE: SessionSourceSelection = {
  key: {
    providerId: "builtin.quickchat",
    sourceType: "quick-chat",
    actionId: "start-session",
    contractVersion: 1,
  },
  input: {},
};

/** The base fields for a new worktree: only what differs from the server's defaults. */
function worktreeBase(state: NewSessionState): WorktreeBaseInput {
  return {
    ...(state.baseBranch ? { baseBranch: state.baseBranch } : {}),
    ...(state.fetchOrigin === false ? { fetchOrigin: false as const } : {}),
  };
}

function repositorySource(
  path: string,
  workspace: NewSessionWorkspace,
  branch: string | undefined,
  base: WorktreeBaseInput,
): SessionSourceSelection {
  const input: Record<string, unknown> = {
    repositoryPath: path,
    isolationStrategy: workspace.kind === "current" ? "existing" : "worktree",
  };
  if (workspace.kind === "existing") {
    input.existingWorktreePath = workspace.path;
  } else if (workspace.kind === "new") {
    Object.assign(input, branch ? { branch } : {}, base);
  }

  return {
    key: {
      providerId: "builtin.repository",
      sourceType: "repository",
      actionId: "start-session",
      contractVersion: 1,
    },
    input,
  };
}

function directorySource(path: string): SessionSourceSelection {
  return {
    key: {
      providerId: "builtin.local",
      sourceType: "directory",
      actionId: "start-session",
      contractVersion: 1,
    },
    input: {
      directory: path,
      isolationStrategy: "existing",
    },
  };
}

/** Turns the new-session page's choices into the arguments for `createSession`. */
export function buildCreateSessionRequest(state: NewSessionState): BuildNewSessionRequestResult {
  const message = state.message.trim();
  const preset = state.gitHubPreset ?? null;
  const { folder, workspace } = state;

  if (preset && folder.kind !== "repository") {
    return { ok: false, error: `Pick the repository for ${preset.repoFullName} #${preset.number}.` };
  }

  if (folder.kind !== "none" && !folder.path.trim()) {
    return { ok: false, error: "Pick a folder." };
  }

  const isWorktree = folder.kind === "repository" && workspace.kind !== "current";
  const isNewWorktree = folder.kind === "repository" && workspace.kind === "new";
  const branch = isNewWorktree ? resolveNewWorktreeBranch(state) : undefined;
  const base = isNewWorktree ? worktreeBase(state) : {};

  let directory: string | undefined;
  let source: SessionSourceSelection;
  switch (folder.kind) {
    case "none":
      directory = undefined;
      source = QUICK_CHAT_SOURCE;
      break;
    case "directory":
      directory = folder.path.trim();
      source = directorySource(directory);
      break;
    case "repository":
      directory = folder.path;
      source = preset
        ? buildGitHubSessionSourceSelection(
          preset,
          folder.path,
          isWorktree ? "worktree" : "existing",
          branch,
          workspace.kind === "existing" ? workspace.path : undefined,
          base,
        )
        : repositorySource(folder.path, workspace, branch, base);
      break;
  }

  const tags = state.tags?.map((tag) => tag.trim()).filter((tag) => tag.length > 0) ?? [];
  const title = state.title?.trim() || preset?.title.trim() || titleFromMessage(message) || undefined;

  return {
    ok: true,
    directory,
    options: {
      source,
      isolationStrategy: isWorktree ? "worktree" : "existing",
      ...(branch ? { branch } : {}),
      ...(title ? { title } : {}),
      ...(message ? { initialPrompt: message } : {}),
      ...(state.harnessType ? { harnessType: state.harnessType } : {}),
      ...(state.harnessProfileId ? { harnessProfileId: state.harnessProfileId } : {}),
      ...(state.projectId ? { projectId: state.projectId } : {}),
      ...(tags.length > 0 ? { tags } : {}),
    },
  };
}

/**
 * The sidebar row for a session the page just created, before the session list or the session's
 * details have it. It lands where the draft row was: the chosen project, or Scratch.
 */
export function buildCreatedSessionRow(
  response: CreateSessionResponse,
  request: NewSessionRequest,
  project: { id: string; name: string } | null,
): SessionListItem {
  const { options } = request;
  const isWorking = Boolean(options.initialPrompt) || options.source?.key.providerId === "builtin.github";
  return {
    instanceId: response.instanceId,
    workspaceId: response.workspaceId,
    workspaceDirectory: request.directory ?? "",
    workspaceDisplayName: null,
    isolationStrategy: options.isolationStrategy ?? "existing",
    sessionStatus: isWorking ? "active" : "idle",
    session: response.session,
    instanceStatus: "running",
    parentSessionId: null,
    sourceDirectory: request.directory ?? null,
    branch: options.branch ?? null,
    activityStatus: isWorking ? "busy" : "idle",
    lifecycleStatus: "running",
    retentionStatus: "active",
    archivedAt: null,
    typedInstanceStatus: "running",
    isHidden: false,
    projectId: project?.id ?? null,
    projectName: project?.name ?? null,
    harnessType: options.harnessType ?? null,
    tags: response.session.tags ?? [],
  };
}
