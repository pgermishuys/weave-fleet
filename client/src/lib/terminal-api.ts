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

/**
 * The setup terminal: a shell in the user's home folder, outside any session, where Fleet types a harness's
 * installer for the user to run. Opening one ends the last one.
 */
export const SETUP_TERMINALS_PATH = "/api/setup/terminals";

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

export async function createSetupTerminal(cols: number, rows: number): Promise<TerminalSummary> {
  const response = await apiFetch(SETUP_TERMINALS_PATH, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ cols, rows }),
  });
  if (!response.ok) throw await failure(response);
  return (await response.json()) as TerminalSummary;
}

export async function closeSetupTerminal(terminalId: string): Promise<void> {
  const response = await apiFetch(`${SETUP_TERMINALS_PATH}/${encodeURIComponent(terminalId)}`, { method: "DELETE" });
  if (!response.ok && response.status !== 404) throw await failure(response);
}

/** The socket for a session's terminal, or for any terminal under `basePath` (e.g. {@link SETUP_TERMINALS_PATH}). */
export function terminalSocketUrl(sessionId: string, terminalId: string, cols: number, rows: number, basePath?: string): string {
  const query = `cols=${Math.round(cols)}&rows=${Math.round(rows)}`;
  return wsUrl(`${basePath ?? terminalsPath(sessionId)}/${encodeURIComponent(terminalId)}/socket?${query}`);
}
