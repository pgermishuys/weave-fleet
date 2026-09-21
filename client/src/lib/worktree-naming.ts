import { slugForBranch } from "@/lib/new-session-request";

/**
 * The composer's copy of the worktree naming resolver, for the plan line as you type. The server
 * does the real naming (`WorktreeNameResolver.cs`); both are asserted against the shared cases in
 * `tests/contracts/worktree-naming-cases.json` so a preview can't promise a name the worktree
 * won't get.
 */

export interface WorktreeNamingTemplates {
  branch: string;
  root: string;
  folder: string;
  capture?: Record<string, string> | null;
  initials?: string | null;
}

/** Which layer set a field: Fleet's default, the user's settings, or the repository's own. */
export type WorktreeNamingLayer = "default" | "user" | "project";

export interface WorktreeNamingContext {
  repositoryPath: string;
  user: string;
  initials?: string | null;
  /** ISO date, `2026-09-21`. */
  date: string;
  shortId: string;
  home: string;
}

export interface WorktreeName {
  /** Null when the template wanted a slug this message couldn't give; the server names it instead. */
  branch: string | null;
  root: string;
  folder: string;
}

export const defaultWorktreeNaming: WorktreeNamingTemplates = {
  branch: "fleet/{slug}",
  root: "{repoParent}/{repo}-worktrees",
  folder: "{branch}",
};

const TOKEN = /\{(\w+)\}/g;
const MAX_CAPTURE_LENGTH = 200;

function baseName(path: string): string {
  return path.replace(/[/\\]+$/, "").split(/[/\\]/).filter(Boolean).pop() ?? path;
}

function parentPath(path: string): string {
  const trimmed = path.replace(/[/\\]+$/, "");
  const cut = trimmed.lastIndexOf("/") >= 0 ? trimmed.lastIndexOf("/") : trimmed.lastIndexOf("\\");
  return cut > 0 ? trimmed.slice(0, cut) : trimmed;
}

function substitute(template: string, values: Record<string, string>): string {
  return template.replace(TOKEN, (_, name: string) => values[name] ?? "");
}

/** Doubled separators, and separators left beside a slash by an empty token. */
function tidy(value: string): string {
  return value.replace(/([-_/])\1+/g, "$1").replace(/[-_]?\/[-_]?/g, "/");
}

function collapse(value: string): string {
  return tidy(value).replace(/^[-_/. ]+/, "").replace(/[-_/. ]+$/, "");
}

/** The same tidying for a path, which keeps its leading separator. */
function collapsePath(value: string): string {
  return tidy(value).replace(/[-_/. ]+$/, "");
}

function readCaptures(
  capture: Record<string, string> | null | undefined,
  message: string,
): Record<string, string> {
  const captures: Record<string, string> = {};
  for (const [name, pattern] of Object.entries(capture ?? {})) {
    captures[name] = "";
    if (!message || !pattern || pattern.length > MAX_CAPTURE_LENGTH) {
      continue;
    }

    try {
      const match = new RegExp(pattern).exec(message);
      if (match) {
        // A group means "this part", no groups means the whole match.
        captures[name] = match[1] ?? match[0];
      }
    } catch {
      // A pattern Settings would have refused; naming carries on without it.
    }
  }

  return captures;
}

function tokenValues(
  naming: WorktreeNamingTemplates,
  context: WorktreeNamingContext,
  captures: Record<string, string>,
  slug: string,
): Record<string, string> {
  return {
    slug,
    repo: baseName(context.repositoryPath),
    user: context.user,
    initials: naming.initials ?? context.initials ?? "",
    date: context.date,
    shortid: context.shortId,
    ...captures,
  };
}

function rootTokenValues(
  naming: WorktreeNamingTemplates,
  context: WorktreeNamingContext,
): Record<string, string> {
  return {
    repo: baseName(context.repositoryPath),
    repoParent: parentPath(context.repositoryPath),
    home: context.home,
    user: context.user,
    initials: naming.initials ?? context.initials ?? "",
    date: context.date,
    shortid: context.shortId,
  };
}

/**
 * Names a worktree for `message`. `branchOverride` is a branch already settled by the caller — a
 * typed name or a source's suggestion — and the folder then follows it.
 */
export function resolveWorktreeName(
  naming: WorktreeNamingTemplates,
  context: WorktreeNamingContext,
  message: string,
  branchOverride?: string | null,
): WorktreeName {
  const captures = readCaptures(naming.capture, message);

  // What a capture consumed is no longer part of the slug, or "{ticket}-{slug}" says it twice
  // ("feature/PLAT-1841-plat-1841-add-rate-limiting").
  let slugSource = message;
  for (const captured of Object.values(captures)) {
    if (captured) {
      slugSource = slugSource.replace(captured, " ");
    }
  }

  const slug = slugForBranch(slugSource);
  const values = tokenValues(naming, context, captures, slug);

  let branch: string | null;
  if (branchOverride) {
    branch = branchOverride;
  } else {
    // A template built around {slug} with nothing to slug would collapse to its prefix alone
    // ("pg"), which git accepts and which collides with the next one.
    branch = naming.branch.includes("{slug}") && slug.length === 0
      ? null
      : collapse(substitute(naming.branch, values)) || null;
  }

  const root = collapsePath(substitute(naming.root, rootTokenValues(naming, context)));
  const folder = collapse(substitute(naming.folder, { ...values, branch: branch ?? "" }))
    .replace(/[/\\]/g, "-");

  return { branch, root, folder };
}

/** `~/src/x` for a path under the home directory, as the plan line shows it. */
export function tildeWorktreePath(path: string, home: string): string {
  return home && path.startsWith(home) ? `~${path.slice(home.length)}` : path;
}
