/**
 * Reading what someone types into the Folder menu as a folder to create or a repository to clone.
 * The server checks everything again; these only decide which rows to offer.
 */

/** A repository to clone, as typed, and the folder it gets by default. */
export interface CloneSource {
  /** What goes to the server: `owner/repo`, or the address as typed. */
  repository: string;
  /** `owner/repo` for GitHub, else the address. */
  label: string;
  /** The repository's name, which the clone's folder is called. */
  name: string;
}

const OWNER_REPO = /^([A-Za-z0-9][A-Za-z0-9-]*)\/([A-Za-z0-9._-]+?)(?:\.git)?$/;
/** A repository's page on github.com, or a page inside it (`/tree/main`, `/pull/12`). */
const GITHUB_URL = /^(?:https:\/\/)?(?:www\.)?github\.com\/([A-Za-z0-9][A-Za-z0-9-]*)\/([A-Za-z0-9._-]+?)(?:\.git)?(?:\/.*)?$/i;
const OTHER_URL = /^(?:https|ssh):\/\/[^\s/]+\/\S+$/;
const SCP_LIKE = /^[A-Za-z0-9._-]+@[A-Za-z0-9.-]+:[A-Za-z0-9._/-]+$/;

function repositoryName(address: string): string {
  const last = address.replace(/\/+$/, "").split(/[/:]/).pop() ?? address;
  return last.replace(/\.git$/i, "");
}

/** A GitHub `owner/repo`, a GitHub URL, or an https/ssh address of any git host; null for anything else. */
export function parseCloneSource(text: string): CloneSource | null {
  const value = text.trim().replace(/\/+$/, "");
  const github = GITHUB_URL.exec(value) ?? OWNER_REPO.exec(value);
  if (github) {
    const [, owner, repo] = github;
    return { repository: `${owner}/${repo}`, label: `${owner}/${repo}`, name: repo };
  }
  if (OTHER_URL.test(value) || SCP_LIKE.test(value)) {
    return { repository: value, label: value, name: repositoryName(value) };
  }
  return null;
}

/** Characters no folder name may have on Windows, macOS or Linux, and control characters. */
const BAD_NAME_CHARACTER = /[<>:"|?*\u0000-\u001f]/;

/** A name or path typed for a new folder: the folders it makes, or what's wrong with it. */
export interface TypedFolderPath {
  /** The folders to make, outermost first: `clients\acme portal` is `["clients", "acme-portal"]`. */
  segments: string[];
  /** A character no folder name can have, when one was typed. Nothing is dropped quietly. */
  invalid: string | null;
}

/**
 * The folders for what was typed: `/` and `\` separate folders, spaces become dashes, case is kept,
 * and `.`/`..` go. Characters no file system allows are reported in `invalid`, not removed.
 */
export function folderPathFrom(text: string): TypedFolderPath {
  const segments = text
    .split(/[/\\]/)
    .map((segment) => segment.trim().replace(/\s+/g, "-").replace(/^[.-]+|[.\s]+$/g, ""))
    .filter(Boolean);
  return { segments, invalid: BAD_NAME_CHARACTER.exec(text)?.[0] ?? null };
}

/** A character no folder name can have in `name`, if there is one. */
export function invalidNameCharacter(name: string): string | null {
  return BAD_NAME_CHARACTER.exec(name)?.[0] ?? null;
}

/** The separator a machine's paths use, judged from one of its paths. */
export function separatorOf(path: string | null | undefined): "/" | "\\" {
  if (!path) {
    return "/";
  }
  return /^[A-Za-z]:\\|^\\\\/.test(path) || (path.includes("\\") && !path.includes("/")) ? "\\" : "/";
}

/** Typed separators, either kind, as the machine writes them. */
export function withSeparator(text: string, separator: "/" | "\\"): string {
  return text.replace(/[/\\]/g, separator);
}

/** Whether typed text names a place by itself (`~`, `/`, `C:\`, `\\server`) rather than one inside a location. */
export function isRootedPath(text: string): boolean {
  return /^(~|\/|\\\\|[A-Za-z]:([/\\]|$))/.test(text);
}

/** Text typed in the folder box: the folder to list (up to the last separator) and the name typed after it. */
export function splitTypedPath(text: string): { folder: string; name: string } {
  const index = Math.max(text.lastIndexOf("/"), text.lastIndexOf("\\"));
  return index < 0 ? { folder: "", name: text } : { folder: text.slice(0, index + 1), name: text.slice(index + 1) };
}

/** The folders between `base`, which is there, and `target` inside it: the ones a create would make. */
export function foldersBetween(base: string, target: string): string[] {
  const trimmed = (value: string) => value.replace(/[/\\]+$/, "");
  return trimmed(target).slice(trimmed(base).length).split(/[/\\]/).filter(Boolean);
}

/** `name` inside `parent`, with the separator the parent already uses. */
export function joinPath(parent: string, name: string): string {
  const separator = parent.includes("\\") && !parent.includes("/") ? "\\" : "/";
  return parent.endsWith(separator) ? `${parent}${name}` : `${parent}${separator}${name}`;
}

function isInside(path: string, root: string): boolean {
  const normalized = (value: string) => value.replace(/[/\\]+$/, "").toLowerCase();
  const child = normalized(path);
  const parent = normalized(root);
  return child === parent || child.startsWith(`${parent}/`) || child.startsWith(`${parent}\\`);
}

/** The workspace root `path` is in, if any. */
export function rootContaining(path: string, roots: readonly string[]): string | null {
  return roots.find((root) => isInside(path, root)) ?? null;
}

/**
 * Where a new folder goes: the root last used for one, else the root of the folder in use, else
 * the first root. Null when there are no roots.
 */
export function defaultNewFolderRoot(
  roots: readonly string[],
  lastRoot: string | null,
  currentPath: string | null,
): string | null {
  if (lastRoot && roots.includes(lastRoot)) {
    return lastRoot;
  }
  return (currentPath ? rootContaining(currentPath, roots) : null) ?? roots[0] ?? null;
}
