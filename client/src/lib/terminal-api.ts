import { apiFetch, wsUrl } from "@/lib/api-client";

/**
 * A terminal tab. `stopped` means it was saved before Fleet restarted: opening
 * its socket starts a new shell under the old scrollback.
 */
export interface TerminalSummary {
  id: string;
  title: string;
  status: "running" | "stopped";
  createdAt: string;
}

/** A refused terminal request, with the server's message for the user. */
export class TerminalApiError extends Error {
  constructor(
    message: string,
    readonly status: number,
  ) {
    super(message);
    this.name = "TerminalApiError";
  }
}

function terminalsPath(sessionId: string): string {
  return `/api/sessions/${encodeURIComponent(sessionId)}/terminals`;
}

async function failure(response: Response): Promise<TerminalApiError> {
  let message = `The terminal request failed (HTTP ${response.status}).`;
  try {
    const body = (await response.json()) as { error?: unknown };
    if (typeof body.error === "string" && body.error) message = body.error;
  } catch {
    // Keep the generic message.
  }
  return new TerminalApiError(message, response.status);
}

export async function listTerminals(sessionId: string): Promise<TerminalSummary[]> {
  const response = await apiFetch(terminalsPath(sessionId));
  if (!response.ok) throw await failure(response);
  const body: unknown = await response.json();
  return Array.isArray(body) ? (body as TerminalSummary[]) : [];
}

export async function createTerminal(sessionId: string, cols: number, rows: number): Promise<TerminalSummary> {
  const response = await apiFetch(terminalsPath(sessionId), {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ cols, rows }),
  });
  if (!response.ok) throw await failure(response);
  return (await response.json()) as TerminalSummary;
}

export async function closeTerminal(sessionId: string, terminalId: string): Promise<void> {
  const response = await apiFetch(`${terminalsPath(sessionId)}/${encodeURIComponent(terminalId)}`, { method: "DELETE" });
  if (!response.ok && response.status !== 404) throw await failure(response);
}

export function terminalSocketUrl(sessionId: string, terminalId: string, cols: number, rows: number): string {
  const query = `cols=${Math.round(cols)}&rows=${Math.round(rows)}`;
  return wsUrl(`${terminalsPath(sessionId)}/${encodeURIComponent(terminalId)}/socket?${query}`);
}
