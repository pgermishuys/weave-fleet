import { defineStore } from "pinia";
import { computed, ref, shallowRef } from "vue";
import type { SessionListItem } from "@/api/client";
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
}

/** A machine that isn't live, as its sidebar group shows it: the last session list it returned. */
export interface MachineSessions {
  sessions: SessionListItem[];
  error: string | null;
  /** When the list last came back, in ms; null before it ever has. */
  loadedAt: number | null;
  loading: boolean;
}

const POLL_INTERVAL_MS = 15_000;
/** How often a live machine that isn't home is asked whether it's still there. */
const LIVE_CHECK_INTERVAL_MS = 10_000;
const SESSIONS_CACHE_KEY = "weave:machine-sessions";

/** Only what a machine group's rows show, so the cache stays small. */
function trimForCache(item: SessionListItem): SessionListItem {
  const { session, instanceId, sessionStatus, activityStatus, retentionStatus, parentSessionId, retryAttempt } = item;
  return {
    session: { id: session.id, title: session.title, time: session.time },
    instanceId,
    sessionStatus,
    activityStatus,
    retentionStatus,
    parentSessionId,
    retryAttempt,
  } as SessionListItem;
}

/** Each machine's last list, so a machine that's away still shows its sessions after a reload. */
function loadCachedSessions(): Record<string, MachineSessions> {
  try {
    const raw = typeof window === "undefined" ? null : window.localStorage.getItem(SESSIONS_CACHE_KEY);
    const cached = raw ? JSON.parse(raw) as Record<string, { sessions: SessionListItem[]; loadedAt: number }> : {};
    return Object.fromEntries(Object.entries(cached).map(([key, value]) => [
      key,
      { sessions: Array.isArray(value.sessions) ? value.sessions : [], error: null, loadedAt: value.loadedAt ?? null, loading: false },
    ]));
  } catch {
    return {};
  }
}

function saveCachedSessions(others: Record<string, MachineSessions>): void {
  try {
    const cached = Object.fromEntries(Object.entries(others)
      .filter(([, value]) => value.loadedAt !== null)
      .map(([key, value]) => [key, { sessions: value.sessions.map(trimForCache), loadedAt: value.loadedAt }]));
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
  const liveMachine = getActiveMachine();
  const liveKey = liveMachine?.id ?? HOME_MACHINE_KEY;

  /**
   * Whether the live machine answers. Home serves the page, so it's taken as there. Another machine is asked every
   * few seconds, and at once when the event hub drops, so the sidebar says it's gone rather than looking live.
   */
  const liveReachable = shallowRef(true);

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
    },
    ...connections.value.map((connection) => ({
      key: connection.id,
      name: connection.name,
      os: connection.os ?? null,
      baseUrl: connection.baseUrl,
      isHome: false,
      isLive: liveKey === connection.id,
      connection,
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
    if (!connection) {
      home.value = info;
      return;
    }
    updateConnection(key, { name: info.name, os: info.os });
  }

  function updateConnection(id: string, patch: Partial<MachineConnection>): void {
    connections.value = connections.value.map((connection) => connection.id === id ? { ...connection, ...patch } : connection);
    persist();
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
  async function loadHomeAccess(): Promise<MachineAccess | null> {
    const response = await fetchOnMachine(null, "/api/machine/access");
    if (response.status === 404) return null;
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

    const current = others.value[key] ?? { sessions: [], error: null, loadedAt: null, loading: false };
    others.value = { ...others.value, [key]: { ...current, loading: true } };

    try {
      const response = await fetchOnMachine(entry.connection, `/api/sessions?limit=${SESSION_PAGE_SIZE}&offset=0`);
      if (response.status === 401) throw new Error(`${entry.name} didn't accept the token.`);
      if (!response.ok) throw new Error(await readError(response, `${entry.name} answered ${response.status}.`));
      const sessions = await response.json() as SessionListItem[];
      if (!entries.value.some((candidate) => candidate.key === key)) return;
      others.value = { ...others.value, [key]: { sessions, error: null, loadedAt: Date.now(), loading: false } };
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

  /** Makes `key`'s machine live and opens `path` there. */
  function openOn(key: string, path: string): void {
    if (key === liveKey) return;
    switchToMachine(key === HOME_MACHINE_KEY ? null : key, path);
  }

  return {
    connections,
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
