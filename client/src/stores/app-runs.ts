import { defineStore } from "pinia";
import { shallowRef } from "vue";
import { apiFetch } from "@/lib/api-client";
import type { AppChangeReason, AppRunStatus, AppUpdated } from "@/lib/domain-events";

/**
 * Apps Fleet runs for sessions, as browser canvases show them. `app.updated` events keep them current (see
 * use-server-canvases.ts); a canvas loads its app once when it opens and after a reconnect. Output isn't pushed:
 * a canvas asks for the lines after the ones it has while it shows them.
 */

export interface AppRun {
  id: string;
  sessionId: string;
  command: string;
  status: AppRunStatus;
  exitCode: number | null;
  /** The page Fleet found, once one answered. */
  url: string | null;
  ports: number[];
  /** Why it last changed, when that came from an event. */
  reason?: AppChangeReason;
}

export interface AppOutput {
  lines: string[];
  /** The `after` to ask with next time. */
  next: number;
}

/** The session's apps, and the command that last served a page in its project (for the + menu). */
export interface SessionApps {
  apps: AppRun[];
  previewCommand: string | null;
}

interface AppRunResponse {
  id: string;
  command: string;
  status: AppRunStatus;
  exitCode: number | null;
  url: string | null;
  ports: number[];
}

/** Fleet keeps this many lines of an app's output; so does the canvas. */
const MAX_OUTPUT_LINES = 2000;

/** The line Fleet writes into an app's output when it starts the app again (AppRunner). */
export const RESTART_MARKER = "── restarted by Fleet ──";

/** The output of the app's current run: the lines after Fleet last started it again. */
export function currentRunLines(lines: readonly string[]): string[] {
  const restart = lines.lastIndexOf(RESTART_MARKER);
  return restart < 0 ? [...lines] : lines.slice(restart + 1);
}

function sessionPath(sessionId: string): string {
  return `/api/sessions/${encodeURIComponent(sessionId)}`;
}

function appPath(sessionId: string, appId: string): string {
  return `${sessionPath(sessionId)}/apps/${encodeURIComponent(appId)}`;
}

function fromResponse(sessionId: string, body: AppRunResponse): AppRun {
  return {
    id: body.id,
    sessionId,
    command: body.command,
    status: body.status,
    exitCode: body.exitCode ?? null,
    url: body.url ?? null,
    ports: body.ports ?? [],
  };
}

async function errorText(response: Response): Promise<string> {
  const body = (await response.json().catch(() => ({}))) as { error?: string };
  return body.error ?? `HTTP ${response.status}`;
}

export const useAppRunsStore = defineStore("app-runs", () => {
  const byId = shallowRef<Record<string, AppRun>>({});
  const outputById = shallowRef<Record<string, AppOutput>>({});

  // Events can overtake a response that was read earlier: a response only lands if no event came after its request.
  let clock = 0;
  const changedAt = new Map<string, number>();

  function set(run: AppRun): void {
    byId.value = { ...byId.value, [run.id]: run };
  }

  function setIfCurrent(run: AppRun, askedAt: number): void {
    if ((changedAt.get(run.id) ?? 0) > askedAt) return;
    set(run);
  }

  function applyEvent(event: AppUpdated): void {
    const { payload } = event;
    changedAt.set(payload.appId, ++clock);
    set({
      id: payload.appId,
      sessionId: payload.sessionId,
      command: payload.command,
      status: payload.status,
      exitCode: payload.exitCode ?? null,
      url: payload.url ?? null,
      ports: payload.ports ?? [],
      reason: payload.reason,
    });
  }

  /** Loads the app as it is now; null when the session has no such app. */
  async function load(sessionId: string, appId: string): Promise<AppRun | null> {
    const askedAt = clock;
    const response = await apiFetch(appPath(sessionId, appId));
    if (response.status === 404) return null;
    if (!response.ok) throw new Error(await errorText(response));

    const run = fromResponse(sessionId, (await response.json()) as AppRunResponse);
    setIfCurrent(run, askedAt);
    return byId.value[appId] ?? run;
  }

  /** Starts (or restarts) or stops the app. Returns why Fleet refused, or null. */
  async function act(sessionId: string, appId: string, action: "restart" | "stop"): Promise<string | null> {
    const askedAt = clock;
    try {
      const response = await apiFetch(`${appPath(sessionId, appId)}/${action}`, { method: "POST" });
      if (!response.ok) return await errorText(response);
      if (response.status === 200) setIfCurrent(fromResponse(sessionId, (await response.json()) as AppRunResponse), askedAt);
      else await load(sessionId, appId);
      return null;
    } catch (error) {
      return error instanceof Error ? error.message : String(error);
    }
  }

  /** Fetches the output after the lines the store has. */
  async function fetchOutput(sessionId: string, appId: string): Promise<void> {
    const current = outputById.value[appId] ?? { lines: [], next: 0 };
    const response = await apiFetch(`${appPath(sessionId, appId)}/output?after=${current.next}`);
    if (!response.ok) return;

    const body = (await response.json()) as AppOutput;
    // Fewer lines than before: Fleet restarted and lost them. Start over from what it has.
    if (body.next < current.next) {
      outputById.value = { ...outputById.value, [appId]: { lines: [], next: 0 } };
      await fetchOutput(sessionId, appId);
      return;
    }
    if (body.lines.length === 0 && body.next === current.next && outputById.value[appId]) return;

    const lines = [...current.lines, ...body.lines];
    outputById.value = {
      ...outputById.value,
      [appId]: { lines: lines.length > MAX_OUTPUT_LINES ? lines.slice(-MAX_OUTPUT_LINES) : lines, next: body.next },
    };
  }

  async function listSessionApps(sessionId: string): Promise<SessionApps> {
    const response = await apiFetch(`${sessionPath(sessionId)}/apps`);
    if (!response.ok) throw new Error(await errorText(response));
    const body = (await response.json()) as { apps: AppRunResponse[]; previewCommand: string | null };
    return { apps: body.apps.map((app) => fromResponse(sessionId, app)), previewCommand: body.previewCommand ?? null };
  }

  /** Runs a command in the session's folder and shows it in a browser canvas. Returns that canvas's id. */
  async function startCommand(sessionId: string, command: string): Promise<string> {
    const askedAt = clock;
    const response = await apiFetch(`${sessionPath(sessionId)}/apps`, {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify({ command }),
    });
    if (!response.ok) throw new Error(await errorText(response));
    const body = (await response.json()) as { app: AppRunResponse; canvasId: string };
    setIfCurrent(fromResponse(sessionId, body.app), askedAt);
    return body.canvasId;
  }

  /** Shows a page on Fleet's machine in a browser canvas. Returns that canvas's id. */
  async function openAddress(sessionId: string, url: string): Promise<string> {
    const response = await apiFetch(`${sessionPath(sessionId)}/browser`, {
      method: "POST",
      headers: { "content-type": "application/json" },
      body: JSON.stringify({ url }),
    });
    if (!response.ok) throw new Error(await errorText(response));
    return ((await response.json()) as { canvasId: string }).canvasId;
  }

  return {
    byId,
    outputById,
    applyEvent,
    load,
    act,
    fetchOutput,
    listSessionApps,
    startCommand,
    openAddress,
  };
});
