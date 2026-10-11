/**
 * The requests behind the draft cards (`ModEndpoints.cs`). Each takes the machine it asks (null: home): a session's
 * drafts go to the session's machine, the Mods switch to the live one.
 */

import { apiFetchOn } from "@/lib/api-client";
import { extractApiError } from "@/lib/api-error";
import type { MachineConnection } from "@/lib/machines";
import type { KeptMod, ModCheckReport, ModDraft, ModsSwitch } from "@/lib/mods/kept";

/** A refused request: the server's own words, and its status (404 while the Mods switch is off). */
export class ModsRequestError extends Error {
  readonly status: number;

  constructor(message: string, status: number) {
    super(message);
    this.name = "ModsRequestError";
    this.status = status;
  }
}

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
  body === undefined
    ? { method }
    : { method, headers: { "Content-Type": "application/json" }, body: JSON.stringify(body) };

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

export const fetchDrafts = (sessionId: string, machine: MachineConnection | null = null) =>
  request<ModDraft[]>(machine, `/api/sessions/${encodeURIComponent(sessionId)}/mods/drafts`);

export const setDraftOn = (sessionId: string, name: string, on: boolean, machine: MachineConnection | null = null) =>
  request<ModDraft>(machine, `${draftPath(sessionId, name)}/${on ? "on" : "off"}`, { method: "POST" });

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
