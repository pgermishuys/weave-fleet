import type { NewSessionFolder, NewSessionWorkspace } from "@/lib/new-session-request";

/** A run of plan-line text; `code` runs are paths and branch names, `warn` runs a caution. */
export interface PlanPart {
  text: string;
  code?: boolean;
  warn?: boolean;
}

export interface NewSessionPlanInput {
  folder: NewSessionFolder | null;
  workspace: NewSessionWorkspace;
  /** Branch the main checkout has checked out, when known. */
  currentBranch: string | null;
  /** Branch a new worktree will get; undefined when the server will name it. */
  newBranch: string | undefined;
  /** Branch of the chosen existing worktree, when known. */
  existingBranch: string | null;
  /** The repository's default branch (`main`), when known. */
  defaultBranch?: string | null;
  /** Where a new worktree starts (`origin/main`), when known. */
  base?: string | null;
  /** Whether an `origin/…` base is fetched first (default true). */
  fetchOrigin?: boolean;
}

/** `/home/me/src/x` → `~/src/x` (also macOS and Windows home folders). */
export function tildePath(path: string): string {
  const match = /^(\/home\/[^/]+|\/Users\/[^/]+|[A-Za-z]:\\Users\\[^\\]+)(?=[/\\]|$)/.exec(path);
  return match ? `~${path.slice(match[1].length)}` : path;
}

function baseName(path: string): string {
  return path.split(/[/\\]/).filter(Boolean).pop() ?? path;
}

/** Where the server will put a new worktree (before any `-2` for a taken folder). */
export function worktreeFolderLabel(repositoryPath: string, branch: string): string {
  return `${baseName(repositoryPath)}-worktrees/${branch.replace(/[/\\]/g, "-")}`;
}

/** One sentence saying what pressing Enter will do. */
export function describeNewSession(input: NewSessionPlanInput): PlanPart[] {
  const { folder, workspace } = input;

  if (folder === null) {
    return [{ text: "Choose where it runs." }];
  }

  if (folder.kind === "none") {
    return [{ text: "Chat only. No folder." }];
  }

  if (folder.kind === "directory") {
    return [{ text: "Runs in " }, { text: tildePath(folder.path), code: true }, { text: " as it is." }];
  }

  if (workspace.kind === "current") {
    const { currentBranch, defaultBranch } = input;
    const offDefault = Boolean(currentBranch && defaultBranch && currentBranch !== defaultBranch);
    return [
      { text: "Works directly in " },
      { text: tildePath(folder.path), code: true },
      ...(currentBranch ? [{ text: " on " }, { text: currentBranch, code: true }] : []),
      offDefault
        ? { text: `. That's not ${defaultBranch}.`, warn: true }
        : { text: ". Edits land in your checkout." },
    ];
  }

  if (workspace.kind === "existing") {
    return [
      { text: "Continues in " },
      { text: tildePath(workspace.path), code: true },
      ...(input.existingBranch ? [{ text: " on " }, { text: input.existingBranch, code: true }] : []),
      { text: "." },
    ];
  }

  const base: PlanPart[] = input.base
    ? [
      { text: input.base, code: true },
      ...(input.fetchOrigin === false && input.base.startsWith("origin/") ? [{ text: " as last fetched" }] : []),
    ]
    : [{ text: "the default branch" }];

  if (!input.newBranch) {
    return [{ text: "New worktree from " }, ...base, { text: ". The branch is named from your message." }];
  }

  return [
    { text: "New worktree " },
    { text: worktreeFolderLabel(folder.path, input.newBranch), code: true },
    { text: " on " },
    { text: input.newBranch, code: true },
    { text: ", from " },
    ...base,
    { text: "." },
  ];
}
