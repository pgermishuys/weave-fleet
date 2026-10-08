// Fleet's REST API with the device key. Shapes come from the web client's OpenAPI types.
import type { components } from "@fleet/api/generated/schema";
import type { PermissionAsk } from "@fleet/composables/use-session-permissions";
import { currentCredentials, type Credentials } from "~/fleet/credentials";

export type SessionListItem = components["schemas"]["SessionListResponse"];
export type { PermissionAsk };

export class FleetError extends Error {
  constructor(message: string, readonly status: number) {
    super(message);
  }
}

async function request<T>(path: string, init: RequestInit = {}, credentials = currentCredentials()): Promise<T> {
  if (!credentials) throw new FleetError("This phone isn't paired with a Fleet.", 401);
  const response = await fetch(credentials.baseUrl + path, {
    ...init,
    headers: { Authorization: `Bearer ${credentials.token}`, "Content-Type": "application/json", ...(init.headers ?? {}) },
  });
  if (!response.ok) {
    const body = (await response.json().catch(() => null)) as { error?: string } | null;
    throw new FleetError(body?.error ?? `Fleet answered ${response.status}.`, response.status);
  }
  // Some answers (a question's, a permission's) are 200 with no body.
  const text = await response.text();
  return (text ? JSON.parse(text) : undefined) as T;
}

const post = <T>(path: string, body: unknown) => request<T>(path, { method: "POST", body: JSON.stringify(body) });
const id = encodeURIComponent;

export const fleet = {
  sessions: () => request<SessionListItem[]>("/api/sessions?limit=50&offset=0"),
  prompt: (sessionId: string, text: string) =>
    post(`/api/sessions/${id(sessionId)}/prompt`, {
      text, agent: null, model: null, attachments: null, userMessageId: null, effort: null,
      correlationId: `native-${Date.now().toString(36)}`,
    }),
  permissions: (sessionId: string) => request<PermissionAsk[]>(`/api/sessions/${id(sessionId)}/permissions`),
  replyPermission: (sessionId: string, requestId: string, reply: "once" | "always" | "reject") =>
    post(`/api/sessions/${id(sessionId)}/permissions/${id(requestId)}`, { reply }),
  answerQuestion: (sessionId: string, requestId: string, answers: string[][]) =>
    post(`/api/sessions/${id(sessionId)}/questions/${id(requestId)}/answer`, { answers }),
};

// ── Pairing: the same versioned QR payload the web app reads (https://<machine>/pair#p=<base64url JSON>) ──

export interface PairingTarget {
  baseUrl: string;
  secret?: string;
  manualCode?: string;
}

/** Reads a scanned or pasted pairing link. Returns null for anything else. */
export function parsePairingLink(text: string): PairingTarget | null {
  const match = /^(https?:\/\/[^#\s]+?)\/pair#p=([A-Za-z0-9_-]+)/.exec(text.trim());
  if (!match) return null;
  try {
    const json = atob(match[2].replace(/-/g, "+").replace(/_/g, "/"));
    const payload = JSON.parse(json) as { v: number; url: string; secret: string };
    return payload.v === 1 ? { baseUrl: payload.url.replace(/\/$/, ""), secret: payload.secret } : null;
  } catch {
    return null;
  }
}

export async function previewPairing(target: PairingTarget): Promise<{ machineId: string; machineName: string; os: string }> {
  const response = await fetch(`${target.baseUrl}/api/pairing/preview`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ secret: target.secret ?? null, manualCode: target.manualCode ?? null }),
  });
  if (!response.ok) throw new FleetError(((await response.json().catch(() => null)) as { error?: string } | null)?.error ?? "That code didn't work.", response.status);
  return response.json();
}

export async function redeemPairing(target: PairingTarget, deviceName: string, platform: string): Promise<Credentials> {
  const response = await fetch(`${target.baseUrl}/api/pairing/redeem`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    credentials: "omit",
    body: JSON.stringify({ secret: target.secret ?? null, manualCode: target.manualCode ?? null, deviceName, platform }),
  });
  if (!response.ok) throw new FleetError(((await response.json().catch(() => null)) as { error?: string } | null)?.error ?? "That code didn't work.", response.status);
  const body = (await response.json()) as { deviceId: string; token: string; machine: { id: string; name?: string | null } };
  return { baseUrl: target.baseUrl, token: body.token, deviceId: body.deviceId, machineId: body.machine.id, machineName: body.machine.name ?? "Fleet" };
}
