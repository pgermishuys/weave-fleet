// What the New session screen starts with, and what the phone remembers between visits. Pure: the storage
// (Keychain/Keystore on a phone) comes in from outside, and nothing here talks to a machine.
import type { HarnessInfo, ModelReference, ScannedRepository, SessionListItem } from "@fleet/api/client";
import { sessionUpdatedAt } from "@fleet/lib/needs-you";
import type { NewSessionFolder } from "@fleet/lib/new-session-request";
import { folderName } from "@fleet/lib/phone/new-session";
import type { KeyValueStorage } from "~/fleet/machines-core";

/** The model a new session starts with: the harness's own (`null`), or one the user picked. */
export type ModelChoice = ModelReference | null;

/** The order a ready harness is taken in when the machine's default isn't ready. */
// OpenCode 1 sits beside OpenCode 2: one family, and the ready one of the two should lead the sheet.
const HARNESS_ORDER = ["opencode2", "opencode", "claude-code", "copilot", "pi"];

const FOLDERS_KEPT = 8;
const MODELS_KEPT = 8;

/** The machine to start on: the one last started on from this phone if it's connected, else the first connected one. */
export function chooseMachine<M extends { machineId: string; state: string }>(machines: readonly M[], last: string | undefined): M | undefined {
  const connected = machines.filter((machine) => machine.state === "connected");
  return connected.find((machine) => machine.machineId === last) ?? connected[0] ?? machines.find((machine) => machine.machineId === last) ?? machines[0];
}

const isReady = (harness: HarnessInfo) => harness.userEnabled !== false && (harness.state ?? (harness.available ? "ready" : "not-working")) === "ready";

/** The harnesses a session can start on, in the order the sheet lists them. */
export function readyHarnesses(harnesses: readonly HarnessInfo[]): HarnessInfo[] {
  const rank = (harness: HarnessInfo) => {
    const at = HARNESS_ORDER.indexOf(harness.type);
    return at === -1 ? HARNESS_ORDER.length : at;
  };
  return harnesses.filter(isReady).sort((a, b) => rank(a) - rank(b));
}

/** The machine's default harness; if it isn't ready, the first ready one in Fleet's order. */
export function chooseHarness(harnesses: readonly HarnessInfo[]): HarnessInfo | undefined {
  const ready = readyHarnesses(harnesses);
  return ready.find((harness) => harness.isDefault) ?? ready[0];
}

/** A session's folder: the repository a worktree session came from, else where it ran. */
export const sessionFolder = (item: SessionListItem): string => item.sourceDirectory || item.workspaceDirectory;

/** The folders to offer first: the phone's own recents, then those of the machine's newest sessions. */
export function recentFolders(phone: readonly string[], sessions: readonly SessionListItem[], limit = FOLDERS_KEPT): string[] {
  const byNewest = [...sessions].filter((item) => !item.parentSessionId && sessionFolder(item)).sort((a, b) => sessionUpdatedAt(b) - sessionUpdatedAt(a));
  return [...new Set([...phone, ...byNewest.map(sessionFolder)])].slice(0, limit);
}

/**
 * The folder to start in: the one last started from this phone on the machine; the first time, that of the machine's
 * newest session. A machine with neither gets none, and the user picks: a folder they never used is never guessed.
 */
export function chooseFolder(phone: readonly string[], sessions: readonly SessionListItem[]): string | undefined {
  return phone[0] ?? recentFolders([], sessions, 1)[0];
}

/** A folder path as the request builder takes it: a scanned repository can get a worktree, any other folder is used as it is. */
export function folderFor(path: string, repositories: readonly ScannedRepository[]): NewSessionFolder {
  return repositories.some((repository) => repository.path === path) ? { kind: "repository", path } : { kind: "directory", path };
}

export const folderLabel = (folder: NewSessionFolder | null, repositories: readonly ScannedRepository[]) =>
  folder ? folderName(folder, repositories) : "Pick a folder";

export interface Blocker {
  /** `error` when starting can't work without a change on the computer; `warning` when the user can change it here. */
  tone: "warning" | "error";
  /** The bold start of the line. */
  lead: string;
  rest: string;
  /** The part of the "where" line to light: it's what to change. */
  part: "machine" | "folder" | "harness";
}

/** Why Send is off before anything is typed, most blocking first; none when it can start. */
export function blockerOf(input: {
  machineName: string; state: string | undefined; otherMachines: boolean; harnessesLoaded: boolean; harnessReady: boolean; hasFolder: boolean;
}): Blocker | null {
  if (input.state !== "connected") {
    const reason = input.state === "signed-out" ? "has removed this phone" : "is offline";
    return {
      tone: "warning", lead: `${input.machineName} ${reason}.`, part: "machine",
      rest: input.state === "signed-out" ? "Pair it again from the Machines tab." : input.otherMachines ? "Pick another machine, or wait for it to come back." : "Wait for it to come back.",
    };
  }
  if (input.harnessesLoaded && !input.harnessReady) {
    return { tone: "error", lead: `No harness is ready on ${input.machineName}.`, rest: "Set one up in Fleet on that computer.", part: "harness" };
  }
  return null;
}

interface MachineRecents {
  folders: string[];
  /** The model last used, by folder and harness. */
  models: Record<string, ModelReference>;
}

const LAST_MACHINE = "fleet.new-session.machine";
// SecureStore keys allow letters, digits, ".", "-" and "_".
const keyOf = (machineId: string) => `fleet.new-session.${machineId.replace(/[^A-Za-z0-9._-]/g, "_")}`;
const modelKey = (folder: string, harness: string) => `${harness}\n${folder}`;

/**
 * What the phone remembers about starting sessions: the machine last started on, and per machine the folders last
 * started in and the model last used in each. Kept on the phone: the machine knows none of it. The draft is kept only
 * while the app is open.
 */
export class NewSessionRecents {
  private lastMachine: string | undefined;
  private machines = new Map<string, MachineRecents>();
  /** The unsent first message: Back from the screen keeps it for the next visit. */
  draft = "";

  constructor(private storage: KeyValueStorage) {}

  async load(machineIds: readonly string[]): Promise<void> {
    this.lastMachine = (await this.storage.get(LAST_MACHINE).catch(() => null)) ?? undefined;
    await Promise.all(machineIds.map(async (id) => {
      const raw = await this.storage.get(keyOf(id)).catch(() => null);
      try {
        const kept = raw ? (JSON.parse(raw) as Partial<MachineRecents>) : {};
        this.machines.set(id, { folders: kept.folders ?? [], models: kept.models ?? {} });
      } catch {
        this.machines.set(id, { folders: [], models: {} });
      }
    }));
  }

  machine = () => this.lastMachine;
  folders = (machineId: string): string[] => this.machines.get(machineId)?.folders ?? [];
  model = (machineId: string, folder: string, harness: string): ModelReference | undefined => this.machines.get(machineId)?.models[modelKey(folder, harness)];

  /** A session was started from the phone. */
  async remember(machineId: string, folder: string | undefined, harness: string, model: ModelChoice): Promise<void> {
    this.lastMachine = machineId;
    const mine = this.machines.get(machineId) ?? { folders: [], models: {} };
    if (folder) {
      mine.folders = [folder, ...mine.folders.filter((path) => path !== folder)].slice(0, FOLDERS_KEPT);
      // Newest last, so the oldest go when there are too many; a session on the harness's own model keeps the last pick.
      if (model) {
        const key = modelKey(folder, harness);
        delete mine.models[key];
        mine.models[key] = model;
      }
      const keys = Object.keys(mine.models);
      for (const stale of keys.slice(0, Math.max(0, keys.length - MODELS_KEPT))) delete mine.models[stale];
    }
    this.machines.set(machineId, mine);
    await this.storage.set(LAST_MACHINE, machineId);
    await this.storage.set(keyOf(machineId), JSON.stringify(mine));
  }
}
