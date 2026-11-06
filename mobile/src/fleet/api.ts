// Fleet's REST API on one machine, with that machine's device key. No React Native in here.
import type { CreateSessionResponse, HarnessCatalog, HarnessInfo, RepositoryScanResponse, RepositoryWorktreesResponse, SessionListItem } from "@fleet/api/client";
import type { NewSessionRequest } from "@fleet/lib/new-session-request";
import type { PermissionAsk } from "@fleet/composables/use-session-permissions";
import type { ServerCanvasSnapshot } from "@fleet/lib/server-canvas";
import type { Machine } from "~/fleet/machines-core";

export type { SessionListItem, PermissionAsk };

export class FleetError extends Error {
  constructor(message: string, readonly status: number) {
    super(message);
  }
}

const id = encodeURIComponent;

/**
 * `onSignedOut` runs on a 401: the computer removed this phone, so the machine reads "signed out" instead of every call
 * failing loudly. `onUnreachable` runs when a read can't reach the machine: it picks again among the machine's
 * addresses (changing `machine.baseUrl`) and says whether it moved, and the read then goes once more.
 */
export function machineApi(machine: Pick<Machine, "baseUrl" | "token">, onSignedOut: () => void = () => {}, onUnreachable: () => Promise<boolean> = async () => false) {
  async function request<T>(path: string, init: RequestInit = {}): Promise<T> {
    const send = () => fetch(machine.baseUrl + path, {
      ...init,
      headers: { Authorization: `Bearer ${machine.token}`, "Content-Type": "application/json", ...(init.headers ?? {}) },
    });
    let response: Response;
    try {
      response = await send().catch(async (error: unknown) => {
        // Only reads go again: a write that never arrived can't be told from one whose answer was lost.
        if ((init.method ?? "GET") !== "GET" || !(await onUnreachable())) throw error;
        return send();
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
    /** What the New session screen reads: the folders Fleet found, a repository's worktrees, the harnesses and what one offers in a folder. */
    repositories: () => request<RepositoryScanResponse>("/api/repositories"),
    worktrees: (path: string) => request<RepositoryWorktreesResponse>(`/api/repositories/worktrees?path=${id(path)}`),
    harnesses: () => request<HarnessInfo[]>("/api/harnesses"),
    catalog: (harnessType: string, directory?: string) =>
      request<HarnessCatalog>(`/api/harnesses/${id(harnessType)}/catalog${directory ? `?directory=${id(directory)}` : ""}`),
    /** Starts a session from the desktop's request builder's answer; the first message goes along as `initialPrompt`. */
    createSession: ({ directory, options }: NewSessionRequest) =>
      post<CreateSessionResponse>("/api/sessions", { directory: directory ?? null, ...options }),
    permissions: (sessionId: string) => request<PermissionAsk[]>(`/api/sessions/${id(sessionId)}/permissions`),
    replyPermission: (sessionId: string, requestId: string, reply: "once" | "always" | "reject") =>
      post(`/api/sessions/${id(sessionId)}/permissions/${id(requestId)}`, { reply }),
    answerQuestion: (sessionId: string, requestId: string, answers: string[][]) =>
      post(`/api/sessions/${id(sessionId)}/questions/${id(requestId)}/answer`, { answers }),
    canvases: (sessionId: string) => request<ServerCanvasSnapshot[]>(`/api/sessions/${id(sessionId)}/canvases`),
    /** Sends the pick made on a waiting page, as the desktop's bar under the page does. */
    sendPageChoice: (sessionId: string, callId: string, value: string, summary: string) =>
      post(`/api/sessions/${id(sessionId)}/page-choices/${id(callId)}/send`, { value, summary }),
    /** After a restart cut a turn off: carry on, drop the card, or stop Fleet continuing sessions by itself. */
    continueInterrupted: (sessionId: string) => post(`/api/sessions/${id(sessionId)}/interruption/continue`, {}),
    dismissInterruption: (sessionId: string) => request(`/api/sessions/${id(sessionId)}/interruption`, { method: "DELETE" }),
    stopAutomatic: () => post("/api/sessions/interruptions/stop-automatic", {}),
    /** Where the machine says it answers now: its Tailscale name and IP, its local network addresses, its host name. */
    machine: () => request<{ id: string; addresses?: { url: string; kind: string }[] | null }>("/api/machine"),
    /** The machines this one is home to, each with its addresses in the owner's order. */
    machines: () => request<{ machines: { id: string; addresses?: { url: string }[] | null }[] }>("/api/machines"),
    /** Saves a machine's addresses in a new order on its home (every one of them, as home lists them). */
    reorderAddresses: (machineId: string, urls: string[]) => request(`/api/machines/${id(machineId)}`, { method: "PUT", body: JSON.stringify({ addresses: urls }) }),
    skipPageChoice: (sessionId: string, callId: string) => post(`/api/sessions/${id(sessionId)}/page-choices/${id(callId)}/skip`, {}),
  };
}

export type MachineApi = ReturnType<typeof machineApi>;
