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

/** A machine the client has been given: where it is and the token to present. The client owns this record. */
export interface MachineConnection {
  /** The machine's own id, from `GET /api/machine`. Stable across restarts, port changes and renames. */
  id: string;
  /** What the machine calls itself; the client refreshes it from `GET /api/machine`. */
  name: string;
  /** Base URL without a trailing slash, e.g. `http://100.64.90.72:2113`. */
  baseUrl: string;
  /** The machine's access token. */
  token: string;
  /** `linux`, `macos`, `windows` or `other`, when known. */
  os?: string;
  addedAt: string;
}

const CATALOG_KEY = "weave:machines";
const ACTIVE_KEY = "weave:active-machine";
const SESSION_MACHINES_KEY = "weave:session-machines";

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
  const base = machine ? machine.baseUrl : homeBase;
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
    return `${toSocket(machine.baseUrl)}${path}${separator}access_token=${encodeURIComponent(machine.token)}`;
  }
  if (homeBase) return `${toSocket(homeBase)}${path}`;
  if (typeof window === "undefined") return path;
  const protocol = window.location.protocol === "https:" ? "wss:" : "ws:";
  return `${protocol}//${window.location.host}${path}`;
}

/** `fetch` against a specific machine, whichever one is live. The sidebar polls other machines with this. */
export function fetchOnMachine(machine: MachineConnection | null, path: string, init?: RequestInit): Promise<Response> {
  return fetch(machineUrl(machine, path), machineRequestInit(machine, init));
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

// ─── Startup and switching ────────────────────────────────────────────────────

/**
 * Decides which machine this page works in, before anything makes a request: the machine that owns the session
 * in the URL when the client knows it, otherwise the one this tab last worked in, otherwise home.
 */
export function restoreActiveMachine(pathname: string = typeof window === "undefined" ? "/" : window.location.pathname): MachineConnection | null {
  const machines = loadMachines();
  const sessionId = /^\/sessions\/([^/]+)$/.exec(pathname)?.[1];
  const owner = sessionId && sessionId !== "new" ? loadSessionMachines()[decodeURIComponent(sessionId)] : undefined;

  let machine: MachineConnection | null;
  if (owner === HOME_MACHINE_KEY) {
    machine = null;
  } else if (owner) {
    machine = machines.find((candidate) => candidate.id === owner) ?? null;
  } else {
    const activeId = loadActiveMachineId();
    machine = activeId ? machines.find((candidate) => candidate.id === activeId) ?? null : null;
  }

  saveActiveMachineId(machine?.id ?? null);
  setActiveMachine(machine);
  return machine;
}

/**
 * Makes `machineId` (null: home) the live machine and opens `path` there. The page reloads, so every store,
 * cache and socket starts over against the new machine.
 */
export function switchToMachine(machineId: string | null, path: string): void {
  saveActiveMachineId(machineId);
  if (typeof window !== "undefined") window.location.assign(path);
}
