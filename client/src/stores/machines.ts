import { defineStore } from "pinia";
import { computed, ref, shallowRef } from "vue";
import type { SessionListItem } from "@/api/client";
import type { ProjectSummary } from "@/lib/session-project-groups";
import { onDisconnect, onReconnect } from "@/composables/use-signalr-socket";
import {
  HOME_MACHINE_KEY,
  fetchOnMachine,
  getActiveMachine,
  loadMachines,
  normalizeBaseUrl,
  rememberSessionMachines,
  saveMachines,
  switchToMachine,
  type MachineConnection,
} from "@/lib/machines";
import { readCredentialsSync } from "@/lib/device-credentials";

/** The machine contract this client speaks (`apiVersion` in `GET /api/machine`). */
export const SUPPORTED_MACHINE_API_VERSION = 1;

/** What `GET /api/machine` says about a machine. */
export interface MachineInfo {
  id: string;
  name: string;
  hostName: string;
  os: string;
  version: string;
  apiVersion: number;
  authMode: string;
  remoteReachable: boolean;
  requiresToken: boolean;
  /** The address phones should use for this machine, when someone saved one. */
  publicUrl?: string | null;
  /** What it can run and how busy it is; missing on a Fleet older than this field. */
  capabilities?: MachineCapabilities | null;
}

/** A harness as a machine last checked it. */
export interface MachineHarness {
  type: string;
  name: string;
  available: boolean;
  /** Whether it's on there (every harness is, until the user turns it off); a new session can use it only when it's available and on. */
  enabled: boolean;
  version: string | null;
}

/** What `GET /api/machine` says a machine can run, and how busy it is. */
export interface MachineCapabilities {
  /** Null until the machine has checked its harnesses once. */
  harnesses: MachineHarness[] | null;
  sessions: { working: number; needsYou: number };
}

/** What `GET /api/machine/access` says: how other devices reach the home machine. */
export interface MachineAccess {
  token: string;
  tokenSource: "saved" | "environment" | "ephemeral";
  host: string;
  port: number;
  remoteReachable: boolean;
  requiresToken: boolean;
  addresses: { url: string; kind: "tailnet" | "lan" | "hostname" }[];
}

/** One row of the machine list: home or a saved machine, with what the client last heard from it. */
export interface MachineEntry {
  /** `HOME_MACHINE_KEY` for home, otherwise the machine's id. */
  key: string;
  name: string;
  os: string | null;
  /** Where it is; empty for home, which is the page's own origin. */
  baseUrl: string;
  isHome: boolean;
  isLive: boolean;
  connection: MachineConnection | null;
  /** What it last said it can run; null until it answers, or when it's too old to say. */
  capabilities: MachineCapabilities | null;
}

/** A machine that isn't live, as its sidebar group shows it: the last session and project lists it returned. */
export interface MachineSessions {
  sessions: SessionListItem[];
  /** Its projects, for their order and names; empty until it first answers. */
  projects: ProjectSummary[];
  error: string | null;
  /** When the list last came back, in ms; null before it ever has. */
  loadedAt: number | null;
  loading: boolean;
}

/** Set once this browser's own list has been copied to the server, so it's never imported twice. */
export const MACHINES_IMPORTED_KEY = "weave:machines-imported";

/** A machine as `GET /api/machines` lists it; the token only for the owner. */
interface ServerMachine {
  id: string;
  name: string;
  baseUrl: string;
  os: string | null;
  status: string;
  addedAt: string;
  lastSeenAt: string | null;
  token: string | null;
}

/**
 * Where the list lives: `server` (home keeps it; this browser caches it), `device` (a paired phone reads it without
 * tokens and uses its own grants), or `local` (home is too old to keep one: this browser's own list, as before).
 */
export type MachineListSource = "pending" | "server" | "device" | "local";

const POLL_INTERVAL_MS = 15_000;
/** How often a live machine that isn't home is asked whether it's still there. */
const LIVE_CHECK_INTERVAL_MS = 10_000;
const SESSIONS_CACHE_KEY = "weave:machine-sessions";

/**
 * Only what a machine's tree shows (its project, its pin, what it came from), so the cache stays small and the tree
 * after a reload looks as it did before.
 */
export function trimForCache(item: SessionListItem): SessionListItem {
  const {
    session, instanceId, sessionStatus, activityStatus, retentionStatus, parentSessionId, retryAttempt, projectId,
    projectName, pinOrder, forkedFromSessionId, spawnedBySessionId, spawnKind, lineageDetachedAt, runningWorkCount,
    workflowRunId,
  } = item;
  return {
    session: { id: session.id, title: session.title, time: session.time },
    instanceId,
    sessionStatus,
    activityStatus,
    retentionStatus,
    parentSessionId,
    retryAttempt,
    projectId,
    projectName,
    pinOrder,
    forkedFromSessionId,
    spawnedBySessionId,
    spawnKind,
    lineageDetachedAt,
    runningWorkCount,
    workflowRunId,
  } as SessionListItem;
}

function trimProject({ id, name, type, position }: ProjectSummary): ProjectSummary {
  return { id, name, type, position };
}

/** Each machine's last list, so a machine that's away still shows its sessions after a reload. */
function loadCachedSessions(): Record<string, MachineSessions> {
  try {
    const raw = typeof window === "undefined" ? null : window.localStorage.getItem(SESSIONS_CACHE_KEY);
    const cached = raw
      ? JSON.parse(raw) as Record<string, { sessions: SessionListItem[]; projects?: ProjectSummary[]; loadedAt: number }>
      : {};
    return Object.fromEntries(Object.entries(cached).map(([key, value]) => [
      key,
      {
        sessions: Array.isArray(value.sessions) ? value.sessions : [],
        projects: Array.isArray(value.projects) ? value.projects : [],
        error: null,
        loadedAt: value.loadedAt ?? null,
        loading: false,
      },
    ]));
  } catch {
    return {};
  }
}

function saveCachedSessions(others: Record<string, MachineSessions>): void {
  try {
    const cached = Object.fromEntries(Object.entries(others)
      .filter(([, value]) => value.loadedAt !== null)
      .map(([key, value]) => [key, {
        sessions: value.sessions.map(trimForCache),
        projects: value.projects.map(trimProject),
        loadedAt: value.loadedAt,
      }]));
    window.localStorage.setItem(SESSIONS_CACHE_KEY, JSON.stringify(cached));
  } catch {
    // The cache is a nicety: without it an unreachable machine just shows no rows.
  }
}
const SESSION_PAGE_SIZE = 100;

async function readError(response: Response, fallback: string): Promise<string> {
  try {
    const body = await response.json() as { error?: string; message?: string };
    return body.error ?? body.message ?? fallback;
  } catch {
    return fallback;
  }
}

function describeFailure(error: unknown, machineName: string): string {
  if (error instanceof TypeError) return `Can't reach ${machineName}.`;
  return error instanceof Error ? error.message : String(error);
}

/** Asks `connection` who it is, checking it speaks this client's contract. */
export async function identifyMachine(baseUrl: string, token: string): Promise<MachineInfo> {
  const probe: MachineConnection = { id: "", name: baseUrl, baseUrl, token, addedAt: "" };
  let response: Response;
  try {
    response = await fetchOnMachine(probe, "/api/machine");
  } catch {
    throw new Error(`Couldn't reach ${baseUrl}. Check the address, that Fleet is running there, and that it listens beyond 127.0.0.1.`);
  }

  if (response.status === 401) throw new Error("That token wasn't accepted.");
  if (response.status === 404) throw new Error("That Fleet is too old to add as a machine. Update it first.");
  if (!response.ok) throw new Error(await readError(response, `The machine answered ${response.status}.`));

  const info = await response.json() as MachineInfo;
  if (info.apiVersion !== SUPPORTED_MACHINE_API_VERSION) {
    throw new Error(`That Fleet speaks machine contract ${info.apiVersion}; this one speaks ${SUPPORTED_MACHINE_API_VERSION}. Update the older one.`);
  }
  if (info.authMode !== "token") throw new Error("That Fleet signs people in with an identity provider, which machines don't support yet.");
  return info;
}

export const useMachinesStore = defineStore("machines", () => {
  const connections = ref<MachineConnection[]>(loadMachines());
  const home = shallowRef<MachineInfo | null>(null);
  const others = ref<Record<string, MachineSessions>>(loadCachedSessions());
  /** What each machine besides home last said about itself (`GET /api/machine`), by id. */
  const identities = shallowRef<Record<string, MachineInfo>>({});
  const liveMachine = getActiveMachine();
  const liveKey = liveMachine?.id ?? HOME_MACHINE_KEY;

  /**
   * Whether the live machine answers. Home serves the page, so it's taken as there. Another machine is asked every
   * few seconds, and at once when the event hub drops, so the sidebar says it's gone rather than looking live.
   */
  const liveReachable = shallowRef(true);

  /** Where the list comes from; see {@link MachineListSource}. */
  const source = shallowRef<MachineListSource>("pending");

  function fromServer(machine: ServerMachine): MachineConnection | null {
    const token = machine.token ?? readCredentialsSync()?.grants.find((grant) => grant.machineId === machine.id)?.token ?? null;
    if (!token) return null;
    return { id: machine.id, name: machine.name, baseUrl: machine.baseUrl, token, os: machine.os ?? undefined, addedAt: machine.addedAt };
  }

  /**
   * Reads the list from home. The first time, machines only this browser knew (from before the list moved to the
   * server) are copied up. Home too old to keep a list (404) leaves this browser's own list in charge.
   */
  async function syncFromServer(): Promise<void> {
    let response: Response;
    try {
      response = await fetchOnMachine(null, "/api/machines");
    } catch {
      source.value = "local";
      return;
    }
    if (!response.ok) {
      source.value = "local";
      return;
    }

    let listed: ServerMachine[];
    try {
      const body = await response.json() as { machines?: unknown };
      if (!Array.isArray(body?.machines)) throw new Error("not a machine list");
      listed = body.machines as ServerMachine[];
    } catch {
      source.value = "local";
      return;
    }
    const owner = listed.every((machine) => machine.token !== null);
    if (!owner) {
      source.value = "device";
      connections.value = listed.map(fromServer).filter((connection): connection is MachineConnection => connection !== null);
      return;
    }

    const missing = connections.value.filter((local) => !listed.some((remote) => remote.id === local.id));
    if (missing.length && !alreadyImported()) {
      const imported = await importToServer(missing);
      if (imported) listed = imported;
    }
    markImported();
    source.value = "server";
    connections.value = listed.map(fromServer).filter((connection): connection is MachineConnection => connection !== null);
    persist();
  }

  function alreadyImported(): boolean {
    try {
      return localStorage.getItem(MACHINES_IMPORTED_KEY) === "true";
    } catch {
      return false;
    }
  }

  function markImported(): void {
    try {
      localStorage.setItem(MACHINES_IMPORTED_KEY, "true");
    } catch {
      // Without storage the next load imports again, which is harmless: import is idempotent.
    }
  }

  async function importToServer(machines: readonly MachineConnection[]): Promise<ServerMachine[] | null> {
    try {
      const response = await fetchOnMachine(null, "/api/machines/import", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify({ machines }),
      });
      return response.ok ? ((await response.json()) as { machines: ServerMachine[] }).machines : null;
    } catch {
      return null;
    }
  }

  /** Copies a change to home's list. The browser already checked the machine, so home just keeps it. */
  async function saveToServer(connection: MachineConnection): Promise<void> {
    if (source.value !== "server") return;
    await importToServer([connection]);
  }

  async function removeFromServer(id: string): Promise<void> {
    if (source.value !== "server") return;
    try {
      await fetchOnMachine(null, `/api/machines/${encodeURIComponent(id)}`, { method: "DELETE" });
    } catch {
      // Home will still list it; the next add or forget tries again.
    }
  }

  const ready = syncFromServer().catch(() => {
    source.value = "local";
  });

  /** Whether the client knows any machine besides home. Everything machine-shaped hides until it does. */
  const hasMachines = computed(() => connections.value.length > 0);

  const entries = computed<MachineEntry[]>(() => [
    {
      key: HOME_MACHINE_KEY,
      name: home.value?.name ?? "This machine",
      os: home.value?.os ?? null,
      baseUrl: "",
      isHome: true,
      isLive: liveKey === HOME_MACHINE_KEY,
      connection: null,
      capabilities: home.value?.capabilities ?? null,
    },
    ...connections.value.map((connection) => ({
      key: connection.id,
      name: connection.name,
      os: connection.os ?? null,
      baseUrl: connection.baseUrl,
      isHome: false,
      isLive: liveKey === connection.id,
      connection,
      capabilities: identities.value[connection.id]?.capabilities ?? null,
    })),
  ]);

  const live = computed(() => entries.value.find((entry) => entry.isLive) ?? entries.value[0]);

  function persist(): void {
    saveMachines(connections.value);
  }

  async function loadHome(): Promise<void> {
    try {
      const response = await fetchOnMachine(null, "/api/machine");
      if (response.ok) home.value = await response.json() as MachineInfo;
    } catch {
      // An older home Fleet has no identity; it's "This machine".
    }
  }

  /** Checks `url` and `token` against the machine and saves it. Returns the saved machine. */
  async function addMachine(url: string, token: string): Promise<MachineConnection> {
    let baseUrl: string;
    try {
      baseUrl = normalizeBaseUrl(url);
    } catch {
      throw new Error("That isn't an address. Try something like http://100.64.90.72:2113.");
    }
    const trimmedToken = token.trim();
    if (!trimmedToken) throw new Error("Paste the machine's access token.");

    const info = await identifyMachine(baseUrl, trimmedToken);
    if (!home.value) await loadHome();
    if (home.value && info.id === home.value.id) throw new Error("That's this machine.");

    const existing = connections.value.find((connection) => connection.id === info.id);
    const connection: MachineConnection = {
      id: info.id,
      name: info.name,
      baseUrl,
      token: trimmedToken,
      os: info.os,
      addedAt: existing?.addedAt ?? new Date().toISOString(),
    };
    connections.value = existing
      ? connections.value.map((candidate) => candidate.id === info.id ? connection : candidate)
      : [...connections.value, connection];
    persist();
    await saveToServer(connection);
    void refreshMachine(connection.id);
    return connection;
  }

  function forgetMachine(id: string): void {
    connections.value = connections.value.filter((connection) => connection.id !== id);
    const rest = { ...others.value };
    delete rest[id];
    others.value = rest;
    saveCachedSessions(rest);
    persist();
    void removeFromServer(id);
    // Working in the machine that's gone: go home.
    if (liveKey === id) switchToMachine(null, "/");
  }

  function connectionFor(key: string): MachineConnection | null {
    return key === HOME_MACHINE_KEY ? null : connections.value.find((connection) => connection.id === key) ?? null;
  }

  /** Renames a machine for every client, on the machine itself. */
  async function renameMachine(key: string, name: string): Promise<void> {
    const connection = connectionFor(key);
    const response = await fetchOnMachine(connection, "/api/machine", {
      method: "PUT",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ name }),
    });
    if (!response.ok) throw new Error(await readError(response, "Couldn't rename the machine."));
    const info = await response.json() as MachineInfo;
    rememberIdentity(key, info);
    if (connection) updateConnection(key, { name: info.name, os: info.os });
  }

  /** Keeps what a machine said about itself, for its row. */
  function rememberIdentity(key: string, info: MachineInfo): void {
    if (key === HOME_MACHINE_KEY) home.value = info;
    else identities.value = { ...identities.value, [key]: info };
  }

  function updateConnection(id: string, patch: Partial<MachineConnection>): void {
    connections.value = connections.value.map((connection) => connection.id === id ? { ...connection, ...patch } : connection);
    persist();
    const updated = connections.value.find((connection) => connection.id === id);
    if (updated) void saveToServer(updated);
  }

  /** Swaps a machine's token for a new one after checking it works. */
  async function updateToken(id: string, token: string): Promise<void> {
    const connection = connectionFor(id);
    if (!connection) return;
    const info = await identifyMachine(connection.baseUrl, token.trim());
    if (info.id !== id) throw new Error(`That address is now a different machine, ${info.name}. Forget this one and add it again.`);
    updateConnection(id, { token: token.trim(), name: info.name, os: info.os });
    void refreshMachine(id);
  }

  /** How other devices reach the home machine. */
  /** How other devices reach home. Null on an older Fleet, or "device" when this browser is a paired device, which may not manage access. */
  async function loadHomeAccess(): Promise<MachineAccess | "device" | null> {
    const response = await fetchOnMachine(null, "/api/machine/access");
    if (response.status === 404) return null;
    if (response.status === 403) return "device";
    if (!response.ok) throw new Error(await readError(response, "Couldn't read this machine's access."));
    return await response.json() as MachineAccess;
  }

  /** Replaces the home machine's token, locking out every device that has the old one. */
  async function replaceHomeToken(): Promise<MachineAccess> {
    const response = await fetchOnMachine(null, "/api/machine/access/token", { method: "POST" });
    if (!response.ok) throw new Error(await readError(response, "Couldn't replace the token."));
    return await response.json() as MachineAccess;
  }

  /** Fetches the session list of a machine that isn't live. */
  async function refreshMachine(key: string): Promise<void> {
    if (key === liveKey) return;
    const entry = entries.value.find((candidate) => candidate.key === key);
    if (!entry) return;

    const current = others.value[key] ?? { sessions: [], projects: [], error: null, loadedAt: null, loading: false };
    others.value = { ...others.value, [key]: { ...current, loading: true } };

    try {
      const [response, projectsResponse, identityResponse] = await Promise.all([
        fetchOnMachine(entry.connection, `/api/sessions?limit=${SESSION_PAGE_SIZE}&offset=0`),
        // Its projects only order and name the groups: without them the sessions still group by the names they carry.
        fetchOnMachine(entry.connection, "/api/projects").catch(() => null),
        // What it can run, for Settings → Machines; the sessions don't need it.
        fetchOnMachine(entry.connection, "/api/machine").catch(() => null),
      ]);
      if (response.status === 401) throw new Error(`${entry.name} didn't accept the token.`);
      if (!response.ok) throw new Error(await readError(response, `${entry.name} answered ${response.status}.`));
      const sessions = await response.json() as SessionListItem[];
      const projects = projectsResponse?.ok
        ? await (projectsResponse.json() as Promise<ProjectSummary[]>).catch(() => current.projects)
        : current.projects;
      const identity = identityResponse?.ok ? await (identityResponse.json() as Promise<MachineInfo>).catch(() => null) : null;
      if (!entries.value.some((candidate) => candidate.key === key)) return;
      if (identity) rememberIdentity(key, identity);
      others.value = { ...others.value, [key]: { sessions, projects, error: null, loadedAt: Date.now(), loading: false } };
      saveCachedSessions(others.value);
      rememberSessionMachines(entry.isHome ? null : key, sessions.map((item) => item.session.id));
    } catch (error) {
      if (!entries.value.some((candidate) => candidate.key === key)) return;
      // Keep the last list: an unreachable machine's sessions stay, marked as such.
      others.value = { ...others.value, [key]: { ...current, error: describeFailure(error, entry.name), loading: false } };
    }
  }

  async function refreshOthers(): Promise<void> {
    await Promise.all(entries.value.filter((entry) => !entry.isLive).map((entry) => refreshMachine(entry.key)));
  }

  /** Records the live machine's sessions, so a link to one opens on this machine later. */
  function rememberLiveSessions(sessionIds: readonly string[]): void {
    if (!hasMachines.value) return;
    rememberSessionMachines(liveMachine?.id ?? null, sessionIds);
  }

  let liveCheckInFlight = false;

  /** Asks the live machine, when it isn't home, whether it's there. */
  async function checkLive(): Promise<void> {
    if (!liveMachine || liveCheckInFlight) return;
    liveCheckInFlight = true;
    try {
      const response = await fetchOnMachine(liveMachine, "/api/machine");
      liveReachable.value = response.ok;
      const info = response.ok ? await (response.json() as Promise<MachineInfo>).catch(() => null) : null;
      if (info) rememberIdentity(liveMachine.id, info);
    } catch {
      liveReachable.value = false;
    } finally {
      liveCheckInFlight = false;
    }
  }

  let pollTimer: ReturnType<typeof setInterval> | undefined;
  let pollers = 0;
  let liveTimer: ReturnType<typeof setInterval> | undefined;
  let stopHubWatch: (() => void)[] = [];

  /** Starts polling machines that aren't live; returns the stop. Several callers share one timer. */
  function startPolling(): () => void {
    pollers += 1;
    if (pollers === 1) {
      void loadHome();
      void refreshOthers();
      pollTimer = setInterval(() => {
        if (typeof document !== "undefined" && document.visibilityState !== "visible") return;
        // Home live isn't among the others; ask it again too, so how busy it is stays current.
        if (liveKey === HOME_MACHINE_KEY) void loadHome();
        void refreshOthers();
      }, POLL_INTERVAL_MS);
      if (liveMachine) {
        void checkLive();
        liveTimer = setInterval(() => void checkLive(), LIVE_CHECK_INTERVAL_MS);
        stopHubWatch = [onDisconnect(() => void checkLive()), onReconnect(() => void checkLive())];
      }
    }
    let stopped = false;
    return () => {
      if (stopped) return;
      stopped = true;
      pollers -= 1;
      if (pollers === 0) {
        if (pollTimer) clearInterval(pollTimer);
        if (liveTimer) clearInterval(liveTimer);
        pollTimer = undefined;
        liveTimer = undefined;
        for (const stop of stopHubWatch) stop();
        stopHubWatch = [];
      }
    };
  }

  /**
   * Makes `key`'s machine live and opens `path` there. The live machine's own list (`liveList`) is kept as its last
   * list first, so after the reload it shows at once, as it did, rather than empty until it's polled.
   */
  function openOn(
    key: string,
    path: string,
    liveList?: { sessions: readonly SessionListItem[]; projects: readonly ProjectSummary[] },
  ): void {
    if (key === liveKey) return;
    if (liveList) {
      saveCachedSessions({
        ...others.value,
        [liveKey]: { sessions: [...liveList.sessions], projects: [...liveList.projects], error: null, loadedAt: Date.now(), loading: false },
      });
    }
    switchToMachine(key === HOME_MACHINE_KEY ? null : key, path);
  }

  return {
    connections,
    source,
    ready,
    syncFromServer,
    home,
    others,
    liveKey,
    hasMachines,
    liveReachable,
    checkLive,
    entries,
    live,
    loadHome,
    addMachine,
    forgetMachine,
    renameMachine,
    updateToken,
    loadHomeAccess,
    replaceHomeToken,
    refreshMachine,
    refreshOthers,
    rememberLiveSessions,
    startPolling,
    openOn,
  };
});
