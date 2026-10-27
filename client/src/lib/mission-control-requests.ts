/**
 * What mission control sends to a session's own machine besides answers (`lib/push/answer.ts`): the nudge, and the
 * reads behind the peek. No Vue.
 */
import type { SessionNow } from "@/lib/mission-control";
import type { AnswerRequest, AnswerTarget } from "@/lib/push/answer";

function headers(target: AnswerTarget): Record<string, string> {
  const result: Record<string, string> = { "Content-Type": "application/json" };
  if (target.token) result.Authorization = `Bearer ${target.token}`;
  return result;
}

/**
 * A nudge: into the running turn when the harness steers (`delivery: "steer"`), otherwise Fleet's own queue, which
 * sends it when the turn ends; a session that isn't working just gets the prompt.
 */
export function nudgeRequest(target: AnswerTarget, sessionId: string, text: string, working: boolean, steers: boolean): AnswerRequest {
  const queued = working && !steers;
  const body = queued ? { text } : { text, userMessageId: crypto.randomUUID(), ...(working ? { delivery: "steer" } : {}) };
  return {
    url: `${target.baseUrl}/api/sessions/${encodeURIComponent(sessionId)}/${queued ? "queue" : "prompt"}`,
    init: { method: "POST", headers: headers(target), credentials: target.baseUrl ? "omit" : "include", body: JSON.stringify(body) },
  };
}

/** A read of a session on its machine. */
export function readRequest(target: AnswerTarget, path: string): AnswerRequest {
  const init: RequestInit = { headers: headers(target), credentials: target.baseUrl ? "omit" : "include" };
  return { url: `${target.baseUrl}${path}`, init };
}

/**
 * What each session on a machine is doing this second. The first call turns the machine's bookkeeping on, so it answers
 * empty once. A machine too old to know it says 404, and one that doesn't answer says nothing: no lines, no error.
 */
export async function fetchNow(target: AnswerTarget): Promise<Record<string, SessionNow>> {
  const request = readRequest(target, "/api/sessions/now");
  try {
    const response = await fetch(request.url, request.init);
    return response.ok ? await response.json() as Record<string, SessionNow> : {};
  } catch {
    return {};
  }
}
