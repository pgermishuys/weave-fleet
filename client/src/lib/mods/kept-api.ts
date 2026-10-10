/**
 * The requests behind Mods in Settings and the draft cards (`ModEndpoints.cs`). Each takes the machine it asks
 * (null: home): kept mods and safe mode go to the live machine, a session's drafts to the session's machine.
 */

import { apiFetchOn } from "@/lib/api-client";
import { extractApiError } from "@/lib/api-error";
import type { MachineConnection } from "@/lib/machines";
import type { KeptMod, ModCheckReport, ModDraft, ModFile, ModsSwitch, ModsView } from "@/lib/mods/kept";

/** A refused request: the server's own words, and its status (404 while the Mods switch is off). */
export class ModsRequestError extends Error {
  readonly status: number;

  constructor(message: string, status: number) {
    super(message);
    this.name = "ModsRequestError";
    this.status = status;
  }
}

const JSON_HEADERS = { "Content-Type": "application/json" };

async function request<T>(machine: MachineConnection | null, path: string, init?: RequestInit): Promise<T> {
  const response = await apiFetchOn(machine, path, init);
  if (!response.ok) {
    let message = `Couldn't reach Mods (${response.status}).`;
    try {
      message = extractApiError(await response.json(), message);
    } catch {
      // Not JSON: the fallback says enough.
    }
    throw new ModsRequestError(message, response.status);
  }
  // A 204 has no body.
  const text = await response.text();
  return (text ? JSON.parse(text) : undefined) as T;
}

const send = (method: string, body?: unknown): RequestInit =>
  body === undefined ? { method } : { method, headers: JSON_HEADERS, body: JSON.stringify(body) };

const modPath = (name: string) => `/api/mods/${encodeURIComponent(name)}`;
const draftPath = (sessionId: string, name: string) =>
  `/api/sessions/${encodeURIComponent(sessionId)}/mods/drafts/${encodeURIComponent(name)}`;

/** The Mods switch and "Start without mods"; null when it can't be read (older server, offline). */
export async function fetchModsSwitch(machine: MachineConnection | null = null): Promise<ModsSwitch | null> {
  try {
    const response = await apiFetchOn(machine, "/api/features/mods");
    if (!response.ok) return null;
    return (await response.json()) as ModsSwitch;
  } catch {
    return null;
  }
}

export const fetchMods = (machine: MachineConnection | null = null) => request<ModsView>(machine, "/api/mods");

export async function fetchModVersionFiles(
  name: string,
  number: number,
  machine: MachineConnection | null = null,
): Promise<ModFile[]> {
  return (await request<{ files: ModFile[] }>(machine, `${modPath(name)}/versions/${number}/files`)).files;
}

export const activateModVersion = (name: string, number: number, machine: MachineConnection | null = null) =>
  request<KeptMod>(machine, `${modPath(name)}/active`, send("PUT", { version: number }));

export const undoMod = (name: string, machine: MachineConnection | null = null) =>
  request<KeptMod>(machine, `${modPath(name)}/undo`, send("POST"));

export const setModOn = (name: string, on: boolean, machine: MachineConnection | null = null) =>
  request<KeptMod>(machine, `${modPath(name)}/${on ? "on" : "off"}`, send("POST"));

export const setSafeMode = (on: boolean, machine: MachineConnection | null = null) =>
  request<ModsView>(machine, "/api/mods/safe-mode", send("PUT", { on }));

export const fetchDrafts = (sessionId: string, machine: MachineConnection | null = null) =>
  request<ModDraft[]>(machine, `/api/sessions/${encodeURIComponent(sessionId)}/mods/drafts`);

export async function fetchDraftFiles(
  sessionId: string,
  name: string,
  machine: MachineConnection | null = null,
): Promise<ModFile[]> {
  return (await request<{ files: ModFile[] }>(machine, `${draftPath(sessionId, name)}/files`)).files;
}

/** The static check's report; null while the server has no checker. */
export async function checkDraft(
  sessionId: string,
  name: string,
  machine: MachineConnection | null = null,
): Promise<ModCheckReport | null> {
  return (await request<{ check: ModCheckReport | null }>(machine, `${draftPath(sessionId, name)}/check`)).check;
}

/** Keeps the draft for all the user's sessions; an empty note sends no body. */
export function keepDraft(
  sessionId: string,
  name: string,
  note: string,
  machine: MachineConnection | null = null,
): Promise<KeptMod> {
  const trimmed = note.trim();
  return request<KeptMod>(machine, `${draftPath(sessionId, name)}/keep`, send("POST", trimmed ? { note: trimmed } : undefined));
}

export const setDraftOn = (sessionId: string, name: string, on: boolean, machine: MachineConnection | null = null) =>
  request<ModDraft>(machine, `${draftPath(sessionId, name)}/${on ? "on" : "off"}`, send("POST"));

/** Drops the agent's "keep this?" request on a draft. 404 counts as done: it may already be gone. */
export async function dismissKeepRequest(
  sessionId: string,
  name: string,
  machine: MachineConnection | null = null,
): Promise<void> {
  try {
    await request<unknown>(machine, `${draftPath(sessionId, name)}/keep-request`, send("DELETE"));
  } catch (error) {
    if (error instanceof ModsRequestError && error.status === 404) return;
    throw error;
  }
}
