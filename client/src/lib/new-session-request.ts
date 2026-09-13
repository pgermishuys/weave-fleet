import type { SessionSourceSelection } from "@/api/client";
import type { CreateSessionOptions } from "@/composables/use-session-actions";
import {
  buildGitHubSessionSourceSelection,
  type GitHubSessionSourcePreset,
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
  gitHubPreset?: GitHubSessionSourcePreset | null;
  /** Branch for a new worktree, instead of the one generated from the message. */
  branch?: string;
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

function repositorySource(path: string, workspace: NewSessionWorkspace, branch: string | undefined): SessionSourceSelection {
  const input: Record<string, unknown> = {
    repositoryPath: path,
    isolationStrategy: workspace.kind === "current" ? "existing" : "worktree",
  };
  if (workspace.kind === "existing") {
    input.existingWorktreePath = workspace.path;
  } else if (workspace.kind === "new" && branch) {
    input.branch = branch;
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
  const branch = folder.kind === "repository" && workspace.kind === "new"
    ? resolveNewWorktreeBranch(state)
    : undefined;

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
        )
        : repositorySource(folder.path, workspace, branch);
      break;
  }

  const tags = state.tags?.map((tag) => tag.trim()).filter((tag) => tag.length > 0) ?? [];
  const title = state.title?.trim() || preset?.title.trim() || undefined;

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
      ...(state.projectId ? { projectId: state.projectId } : {}),
      ...(tags.length > 0 ? { tags } : {}),
    },
  };
}
