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

/**
 * The folder name for what was typed: spaces become dashes, and characters no file system allows
 * go. Case is kept. Empty when nothing usable is left.
 */
export function folderNameFrom(text: string): string {
  return text
    .trim()
    .replace(/[<>:"/\\|?*\u0000-\u001f]/g, "")
    .replace(/\s+/g, "-")
    .replace(/^[.-]+|[.\s]+$/g, "");
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
