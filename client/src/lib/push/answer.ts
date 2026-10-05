/**
 * Answering an agent's ask on any machine: the request the inbox, the phone session view and the service worker's
 * notification buttons all send. Same bodies as the desktop cards. No Vue and no `window`: the service worker
 * imports this.
 */

export type PermissionReply = "once" | "always" | "reject";

/** Where to send it: a base URL ("" for this origin) and the phone's token there (null: the cookie does). */
export interface AnswerTarget {
  baseUrl: string;
  token: string | null;
}

export interface AnswerRequest {
  url: string;
  init: RequestInit;
}

function headers(token: string | null): Record<string, string> {
  const result: Record<string, string> = { "Content-Type": "application/json" };
  if (token) result.Authorization = `Bearer ${token}`;
  return result;
}

function credentialsFor(target: AnswerTarget): RequestCredentials {
  // Another machine takes the token alone; cookies never go cross-origin.
  return target.baseUrl ? "omit" : "include";
}

/** `POST /api/sessions/{id}/permissions/{requestId}`: once, always or reject (with words for the agent). */
export function permissionAnswerRequest(
  target: AnswerTarget,
  sessionId: string,
  requestId: string,
  reply: PermissionReply,
  message?: string | null,
): AnswerRequest {
  return {
    url: `${target.baseUrl}/api/sessions/${encodeURIComponent(sessionId)}/permissions/${encodeURIComponent(requestId)}`,
    init: {
      method: "POST",
      headers: headers(target.token),
      credentials: credentialsFor(target),
      body: JSON.stringify(message ? { reply, message } : { reply }),
    },
  };
}

/** `POST /api/sessions/{id}/questions/{requestId}/answer`: the picked labels per question. */
export function questionAnswerRequest(target: AnswerTarget, sessionId: string, requestId: string, answers: string[][]): AnswerRequest {
  return {
    url: `${target.baseUrl}/api/sessions/${encodeURIComponent(sessionId)}/questions/${encodeURIComponent(requestId)}/answer`,
    init: { method: "POST", headers: headers(target.token), credentials: credentialsFor(target), body: JSON.stringify({ answers }) },
  };
}

/** `POST /api/sessions/{id}/questions/{requestId}/reject`. */
export function questionRejectRequest(target: AnswerTarget, sessionId: string, requestId: string): AnswerRequest {
  return {
    url: `${target.baseUrl}/api/sessions/${encodeURIComponent(sessionId)}/questions/${encodeURIComponent(requestId)}/reject`,
    init: { method: "POST", headers: headers(target.token), credentials: credentialsFor(target) },
  };
}

/** `PATCH /api/sessions/{id}/retention`: archive a session (swiped away on the phone), or bring it back (Undo). */
export function retentionRequest(target: AnswerTarget, sessionId: string, archived: boolean): AnswerRequest {
  return {
    url: `${target.baseUrl}/api/sessions/${encodeURIComponent(sessionId)}/retention`,
    init: {
      method: "PATCH",
      headers: headers(target.token),
      credentials: credentialsFor(target),
      body: JSON.stringify({ retentionStatus: archived ? "archived" : "active" }),
    },
  };
}

/** Why an answer didn't go through, in words; `gone` when the ask was already answered or isn't there. */
export interface AnswerOutcome {
  ok: boolean;
  gone: boolean;
  error: string | null;
}

export async function sendAnswer(request: AnswerRequest, fetcher: typeof fetch = fetch): Promise<AnswerOutcome> {
  let response: Response;
  try {
    response = await fetcher(request.url, request.init);
  } catch {
    return { ok: false, gone: false, error: "Couldn't reach the machine." };
  }
  if (response.ok) return { ok: true, gone: false, error: null };
  if (response.status === 404 || response.status === 409) return { ok: false, gone: true, error: "Already answered." };
  let message = `It answered ${response.status}.`;
  try {
    const body = await response.json() as { error?: string; detail?: string; title?: string };
    message = body.error ?? body.detail ?? body.title ?? message;
  } catch {
    // Keep the status.
  }
  return { ok: false, gone: false, error: message };
}
