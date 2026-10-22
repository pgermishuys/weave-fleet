// Fleet's REST API on one machine, with that machine's device key. No React Native in here.
import type { SessionListItem } from "@fleet/api/client";
import type { PermissionAsk } from "@fleet/composables/use-session-permissions";
import type { Machine } from "~/fleet/machines-core";

export type { SessionListItem, PermissionAsk };

export class FleetError extends Error {
  constructor(message: string, readonly status: number) {
    super(message);
  }
}

const id = encodeURIComponent;

/** `onSignedOut` runs on a 401: the computer removed this phone, so the machine reads "signed out" instead of every call failing loudly. */
export function machineApi(machine: Pick<Machine, "baseUrl" | "token">, onSignedOut: () => void = () => {}) {
  async function request<T>(path: string, init: RequestInit = {}): Promise<T> {
    let response: Response;
    try {
      response = await fetch(machine.baseUrl + path, {
        ...init,
        headers: { Authorization: `Bearer ${machine.token}`, "Content-Type": "application/json", ...(init.headers ?? {}) },
      });
    } catch {
      throw new FleetError("Fleet didn't answer.", 0);
    }
    if (response.status === 401) {
      onSignedOut();
      throw new FleetError("The computer removed this phone.", 401);
    }
    if (!response.ok) {
      const body = (await response.json().catch(() => null)) as { error?: string } | null;
      throw new FleetError(body?.error ?? `Fleet answered ${response.status}.`, response.status);
    }
    // Some answers (a question's, a permission's) are 200 with no body.
    const text = await response.text();
    return (text ? JSON.parse(text) : undefined) as T;
  }
  const post = <T>(path: string, body: unknown) => request<T>(path, { method: "POST", body: JSON.stringify(body) });

  return {
    sessions: () => request<SessionListItem[]>("/api/sessions?limit=100&offset=0"),
    prompt: (sessionId: string, text: string) =>
      post(`/api/sessions/${id(sessionId)}/prompt`, {
        text, agent: null, model: null, attachments: null, userMessageId: null, effort: null,
        correlationId: `phone-${Date.now().toString(36)}`,
      }),
    permissions: (sessionId: string) => request<PermissionAsk[]>(`/api/sessions/${id(sessionId)}/permissions`),
    replyPermission: (sessionId: string, requestId: string, reply: "once" | "always" | "reject") =>
      post(`/api/sessions/${id(sessionId)}/permissions/${id(requestId)}`, { reply }),
    answerQuestion: (sessionId: string, requestId: string, answers: string[][]) =>
      post(`/api/sessions/${id(sessionId)}/questions/${id(requestId)}/answer`, { answers }),
  };
}

export type MachineApi = ReturnType<typeof machineApi>;
