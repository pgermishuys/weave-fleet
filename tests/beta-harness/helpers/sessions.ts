/**
 * Session helpers: create a session pinned to a scenario id, send a prompt, etc.
 *
 * These wrap the fleet REST API so Claude can drive flows from short scripts
 * without restating the JSON shape every time.
 */

export interface NewSessionOptions {
  baseUrl: string;
  scenarioId: string;
  /** Optional working directory; defaults to OS temp. */
  directory?: string;
  /** Optional title. */
  title?: string;
}

export interface CreatedSession {
  sessionId: string;
  instanceId: string;
  workspaceId: string;
}

export async function newSession(opts: NewSessionOptions): Promise<CreatedSession> {
  const body = {
    directory: opts.directory ?? null,
    title: opts.title ?? `beta:${opts.scenarioId}`,
    scenarioId: opts.scenarioId,
  };

  const resp = await fetch(`${opts.baseUrl}/api/sessions`, {
    method: "POST",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(body),
  });
  if (!resp.ok) {
    const text = await resp.text();
    throw new Error(`POST /api/sessions failed (${resp.status}): ${text}`);
  }
  const data = (await resp.json()) as {
    instanceId: string;
    workspaceId: string;
    session: { id: string };
  };
  return {
    sessionId: data.session.id,
    instanceId: data.instanceId,
    workspaceId: data.workspaceId,
  };
}

export interface SendPromptOptions {
  baseUrl: string;
  sessionId: string;
  text: string;
  agent?: string;
  /** Provider/model identifier in fleet "provider/model" form. */
  model?: string;
}

export async function sendPrompt(opts: SendPromptOptions): Promise<void> {
  const body = {
    text: opts.text,
    agent: opts.agent ?? null,
    model: opts.model ?? null,
  };
  const resp = await fetch(
    `${opts.baseUrl}/api/sessions/${encodeURIComponent(opts.sessionId)}/prompt`,
    {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(body),
    },
  );
  if (!resp.ok) {
    const text = await resp.text();
    throw new Error(`POST /prompt failed (${resp.status}): ${text}`);
  }
}
