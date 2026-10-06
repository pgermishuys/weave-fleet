/**
 * The choices on the phone's New session page that aren't the desktop's: which machines the phone can start a session
 * on, and the folder list as one sheet. No Vue.
 */
import type { ScannedRepository } from "@/api/client";
import type { DeviceCredentials } from "@/lib/device-credentials";
import type { MachineConnection } from "@/lib/machines";
import type { NewSessionFolder } from "@/lib/new-session-request";
import type { ListedMachine } from "@/lib/phone/grants";

/** A machine the phone can start a session on. `connection` is null for home, which the phone reaches with its cookie. */
export interface PhoneMachine {
  id: string;
  name: string;
  connection: MachineConnection | null;
}

/**
 * Home first, then every machine the phone has its own key for, named as home's list names them. A machine without a
 * key isn't offered: the phone couldn't reach it.
 */
export function phoneMachines(credentials: DeviceCredentials | null, listed: readonly ListedMachine[]): PhoneMachine[] {
  if (!credentials) return [];
  const home: PhoneMachine = { id: credentials.homeMachineId, name: credentials.homeMachineName, connection: null };
  const others = credentials.grants
    .filter((grant) => grant.machineId !== credentials.homeMachineId)
    .map((grant): PhoneMachine => {
      const name = listed.find((machine) => machine.id === grant.machineId)?.name ?? hostOf(grant.baseUrl);
      return {
        id: grant.machineId,
        name,
        connection: { id: grant.machineId, name, baseUrl: grant.baseUrl, token: grant.token, addedAt: credentials.pairedAt },
      };
    })
    .sort((a, b) => a.name.localeCompare(b.name));
  return [home, ...others];
}

function hostOf(baseUrl: string): string {
  try {
    return new URL(baseUrl).hostname.split(".")[0] || baseUrl;
  } catch {
    return baseUrl;
  }
}

/** A folder as one id, for a list to pick from. */
export function folderId(folder: NewSessionFolder): string {
  return folder.kind === "none" ? "none" : `${folder.kind}:${folder.path}`;
}

export function folderFromId(id: string): NewSessionFolder {
  if (id.startsWith("repository:")) return { kind: "repository", path: id.slice("repository:".length) };
  if (id.startsWith("directory:")) return { kind: "directory", path: id.slice("directory:".length) };
  return { kind: "none" };
}

/** What a folder is called: the repository's name, else the folder's last part. */
export function folderName(folder: NewSessionFolder, repositories: readonly ScannedRepository[]): string {
  if (folder.kind === "none") return "No folder";
  const repository = repositories.find((candidate) => candidate.path === folder.path);
  if (repository) return repository.name;
  return folder.path.split(/[\\/]/).filter(Boolean).pop() ?? folder.path;
}

export interface FolderOption {
  id: string;
  label: string;
  detail?: string;
}

/**
 * The folder sheet: recently used folders first, then the rest of the machine's repositories by name, then no folder
 * (a chat that doesn't touch any files).
 */
export function folderOptions(repositories: readonly ScannedRepository[], recent: readonly NewSessionFolder[]): FolderOption[] {
  const options: FolderOption[] = [];
  const seen = new Set<string>();
  const add = (folder: NewSessionFolder, detail?: string): void => {
    const id = folderId(folder);
    if (seen.has(id)) return;
    seen.add(id);
    options.push({ id, label: folderName(folder, repositories), ...(detail ? { detail } : {}) });
  };

  for (const folder of recent) {
    if (folder.kind !== "none") add(folder, folder.path);
  }
  for (const repository of [...repositories].sort((a, b) => a.name.localeCompare(b.name))) {
    add({ kind: "repository", path: repository.path }, repository.path);
  }
  add({ kind: "none" }, "Just chat; no files");
  return options;
}

/** A piece of the line under the New session chips; the names are bold. */
export interface CaptionPart {
  text: string;
  bold?: boolean;
}

/**
 * "Runs on **hangar**, in a new worktree of **weave-fleet**, with OpenCode 2.": what Start will do, in one sentence
 * under the chips.
 */
export function startCaption(input: { machine: string; folder: NewSessionFolder | null; folderName: string; workspace: { kind: "current" | "new" | "existing"; path?: string }; harness: string | null }): CaptionPart[] {
  const parts: CaptionPart[] = [{ text: "Runs on " }, { text: input.machine, bold: true }];
  const folder = input.folder;
  if (folder && folder.kind !== "none") {
    if (folder.kind === "repository" && input.workspace.kind === "new") parts.push({ text: ", in a new worktree of " });
    else if (folder.kind === "repository" && input.workspace.kind === "existing") parts.push({ text: `, in the ${input.workspace.path?.split(/[\\/]/).filter(Boolean).pop() ?? "existing"} worktree of ` });
    else parts.push({ text: ", straight in " });
    parts.push({ text: input.folderName, bold: true });
  }
  parts.push({ text: input.harness ? `, with ${input.harness}.` : "." });
  return parts;
}
