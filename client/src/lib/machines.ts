/**
 * Machines: other Fleets this client can reach, and which one the app is working in.
 *
 * Every machine runs its own Fleet and keeps its own sessions. The page is always served by one of them, the
 * home machine, and talks to it same-origin with its cookie. Another machine is reached cross-origin with its
 * access token (`Authorization: Bearer`, or `access_token` on a socket) and no cookies. See docs/machines.md.
 *
 * One machine is live at a time: every request the app makes goes to it. Switching reloads the page (see
 * `switchToMachine`), because the app keeps a lot of per-machine state in modules and stores, and a reload is
 * the one switch that can't show one machine's data against another.
 *
 * This module has no Vue in it on purpose: it is the transport rule every request path shares.
 */

import type { MachineDiagnosis } from "@/lib/machine-diagnosis";
import { baseUrlOf, repickAddress, type MachineAddress } from "@/lib/machine-routes";

/** A machine the client has been given: where it is and the token to present. The client owns this record. */
export interface MachineConnection {
  /** The machine's own id, from `GET /api/machine`. Stable across restarts, port changes and renames. */
  id: string;
  /** What the machine calls itself; the client refreshes it from `GET /api/machine`. */
  name: string;
  /**
   * Base URL without a trailing slash, e.g. `http://100.64.90.72:2113`: the address home uses. Requests go to
   * `baseUrlOf(machine)`, the one this tab picked from `addresses` (see `@/lib/machine-routes`).
   */
  baseUrl: string;
  /** Every address the machine can be reached at, in the order to try them; missing from an older home. */
  addresses?: MachineAddress[] | null;
  /** The machine's access token. */
  token: string;
  /** `linux`, `macos`, `windows` or `other`, when known. */
  os?: string;
  addedAt: string;
  /** `user@host` when home reaches it over SSH; missing for a machine added another way, or from an older home. */
  sshDestination?: string | null;
  /** Whether the SSH tunnel is up; null or missing when it isn't an SSH machine. */
  tunnelUp?: boolean | null;
  /** Why home last couldn't reach it, while it can't; missing from an older home. */
  diagnosis?: MachineDiagnosis | null;
}

const CATALOG_KEY = "weave:machines";
const ACTIVE_KEY = "weave:active-machine";
const SESSION_MACHINES_KEY = "weave:session-machines";
const CURRENT_KEY = "weave:current-machine";

/** The machine requests go to; null is the home machine, the Fleet that served the page. */
let activeMachine: MachineConnection | null = null;

export function getActiveMachine(): MachineConnection | null {
  return activeMachine;
}

/** Points every request at `machine` (null: home). Only the page's startup and tests call this; see `switchToMachine`. */
export function setActiveMachine(machine: MachineConnection | null): void {
  activeMachine = machine;
}

export function normalizeBaseUrl(url: string): string {
  let trimmed = url.trim();
  if (!/^https?:\/\//i.test(trimmed)) trimmed = `http://${trimmed}`;
  const parsed = new URL(trimmed);
  return `${parsed.protocol}//${parsed.host}${parsed.pathname.replace(/\/+$/, "")}`;
}

// ─── Requests ─────────────────────────────────────────────────────────────────

/** The URL for `path` on `machine`, or on the home machine through `homeBase` (empty: same origin). */
export function machineUrl(machine: MachineConnection | null, path: string, homeBase = ""): string {
  const base = machine ? baseUrlOf(machine) : homeBase;
  return base ? `${base}${path}` : path;
}

/**
 * The fetch options for a request to `machine`. A remote machine gets its token and no cookies: the token is
 * the whole credential, and a cross-origin request that sent cookies would need the server to allow
 * credentials, which Fleet never does for another origin.
 */
export function machineRequestInit(machine: MachineConnection | null, init: RequestInit = {}): RequestInit {
  if (!machine) {
    return { ...init, credentials: init.credentials ?? "include" };
  }
  const headers = new Headers(init.headers);
  headers.set("Authorization", `Bearer ${machine.token}`);
  headers.delete("X-CSRF-Token");
  return { ...init, headers, credentials: "omit" };
}

/** A WebSocket URL for `path` on `machine`. A browser socket can't set headers, so a remote one carries its token in the query. */
export function machineSocketUrl(machine: MachineConnection | null, path: string, homeBase = ""): string {
  const toSocket = (base: string) => base.replace(/^http(s?):\/\//, (_, s: string) => `ws${s}://`);
  if (machine) {
    const separator = path.includes("?") ? "&" : "?";
    return `${toSocket(baseUrlOf(machine))}${path}${separator}access_token=${encodeURIComponent(machine.token)}`;
  }
  if (homeBase) return `${toSocket(homeBase)}${path}`;
  if (typeof window === "undefined") return path;
  const protocol = window.location.protocol === "https:" ? "wss:" : "ws:";
  return `${protocol}//${window.location.host}${path}`;
}

/**
 * A bare, Vue-free probe of a machine (the sidebar polls with it); the app's API requests use `apiFetchOn`. A read
 * that can't reach the machine is tried once more when another of its addresses answers.
 */
export async function fetchOnMachine(machine: MachineConnection | null, path: string, init?: RequestInit): Promise<Response> {
  try {
    return await fetch(machineUrl(machine, path), machineRequestInit(machine, init));
  } catch (error) {
    if (!machine || !isRead(init?.method) || !(await repickAddress(machine))) throw error;
    return fetch(machineUrl(machine, path), machineRequestInit(machine, init));
  }
}

/** Requests that can safely go again: they change nothing there. */
export function isRead(method: string | undefined): boolean {
  return ["GET", "HEAD"].includes((method ?? "GET").toUpperCase());
}

// ─── Storage ──────────────────────────────────────────────────────────────────

function readJson<T>(storage: Storage | undefined, key: string, fallback: T): T {
  try {
    const raw = storage?.getItem(key);
    return raw ? (JSON.parse(raw) as T) : fallback;
  } catch {
    return fallback;
  }
}

function writeJson(storage: Storage | undefined, key: string, value: unknown): void {
  try {
    storage?.setItem(key, JSON.stringify(value));
  } catch {
    // Private windows and full quotas: the list lasts until the page closes.
  }
}

const local = () => (typeof window === "undefined" ? undefined : window.localStorage);
const perTab = () => (typeof window === "undefined" ? undefined : window.sessionStorage);

function isConnection(value: unknown): value is MachineConnection {
  const candidate = value as Partial<MachineConnection> | null;
  return typeof candidate?.id === "string"
    && typeof candidate.baseUrl === "string"
    && typeof candidate.token === "string"
    && typeof candidate.name === "string";
}

/** The machines this client has been given, in the order they were added. The home machine isn't in it. */
export function loadMachines(): MachineConnection[] {
  const stored = readJson<unknown>(local(), CATALOG_KEY, []);
  return Array.isArray(stored) ? stored.filter(isConnection) : [];
}

export function saveMachines(machines: readonly MachineConnection[]): void {
  writeJson(local(), CATALOG_KEY, machines);
}

/** Which machine this tab was working in, so a reload stays there. Per tab: another tab starts at home. */
export function loadActiveMachineId(): string | null {
  const id = readJson<unknown>(perTab(), ACTIVE_KEY, null);
  return typeof id === "string" ? id : null;
}

export function saveActiveMachineId(id: string | null): void {
  if (id === null) {
    try {
      perTab()?.removeItem(ACTIVE_KEY);
    } catch {
      // Nothing to forget.
    }
    return;
  }
  writeJson(perTab(), ACTIVE_KEY, id);
}

/** Which machine each session lives on, as far as this client has seen, so a link to a session opens on the right one. */
export function loadSessionMachines(): Record<string, string> {
  const stored = readJson<unknown>(local(), SESSION_MACHINES_KEY, {});
  return stored && typeof stored === "object" && !Array.isArray(stored) ? (stored as Record<string, string>) : {};
}

/**
 * Records that `sessionIds` live on `machineId` (null: home). Entries are only added or moved, never dropped,
 * so a link to an archived session still finds its machine. The map holds ids only and stays small.
 */
export function rememberSessionMachines(machineId: string | null, sessionIds: readonly string[]): void {
  const key = machineId ?? HOME_MACHINE_KEY;
  const current = loadSessionMachines();
  if (sessionIds.every((sessionId) => current[sessionId] === key)) return;
  const next = { ...current };
  for (const sessionId of sessionIds) next[sessionId] = key;
  writeJson(local(), SESSION_MACHINES_KEY, next);
}

/** How `rememberSessionMachines` records the home machine, whose id the client may not know. */
export const HOME_MACHINE_KEY = "home";

/** The user preference behind "Keep every machine live" in Settings → Labs: a live feed per machine, not a poll. */
export const LIVE_MACHINES_PREFERENCE_KEY = "LiveMachines";

/** Which machine this tab was last on, so a reload comes back to it. Per tab; an unlisted key is ignored by the store. */
export function loadCurrentMachineKey(): string | null {
  const key = readJson<unknown>(perTab(), CURRENT_KEY, null);
  return typeof key === "string" ? key : null;
}

export function saveCurrentMachineKey(key: string): void {
  writeJson(perTab(), CURRENT_KEY, key);
}

// ─── Startup and switching ────────────────────────────────────────────────────

/**
 * Decides which machine this page works in, before anything makes a request: always home, the Fleet that served the
 * page. Another machine's sessions open in place (`provideMachineTarget`), so the live machine only changes when
 * the live one is forgotten (`switchToMachine`). A machine an older version stored as this tab's active one is dropped.
 */
export function restoreActiveMachine(): null {
  saveActiveMachineId(null);
  setActiveMachine(null);
  return null;
}

/**
 * Adds a machine the phone holds a key for (a device grant) to this browser's list, so a page opened on it at
 * `/phone/s/<machine>/…` finds it. A machine already listed keeps its entry.
 */
export function rememberPhoneMachine(machine: MachineConnection): void {
  const machines = loadMachines();
  const existing = machines.find((candidate) => candidate.id === machine.id);
  if (existing && existing.token === machine.token) return;
  saveMachines(existing
    ? machines.map((candidate) => candidate.id === machine.id ? { ...candidate, baseUrl: machine.baseUrl, token: machine.token } : candidate)
    : [...machines, machine]);
}

/**
 * Makes `machineId` (null: home) the live machine and opens `path` there. The page reloads, so every store,
 * cache and socket starts over against the new machine.
 */
export function switchToMachine(machineId: string | null, path: string): void {
  saveActiveMachineId(machineId);
  if (typeof window !== "undefined") window.location.assign(path);
}
