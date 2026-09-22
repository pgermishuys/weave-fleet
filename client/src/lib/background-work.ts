/**
 * Work OpenCode 2 moved into the background. A `shell` or `subagent` call made with `background: true` — or any call
 * backgrounded through `POST /api/session/{id}/background` — returns at once with a handle while its work goes on, so
 * its card stays running and says `Background`. Fleet marks such a call `background: true`, and the handle its work
 * carries on under is in the call's metadata:
 *
 *   shell:    { status: "running", background: true, metadata: { shellID: "sh_…" } }
 *   subagent: { status: "running", background: true, metadata: { sessionID: "ses_…" } }
 *
 * When the work really finishes, V2 posts a notice into the session, which Fleet shows as a message of its own. The
 * notice is the text V2 gave the model, and it names the same handle:
 *
 *   <shell id="sh_…" state="completed" command="npm test">
 *   …output…
 *   </shell>
 *
 *   <subagent sessionID="ses_…" state="completed" description="Review the diff">
 *   …what it said…
 *   </subagent>
 *
 * V2 never updates the call itself, so a card is finished by its notice: the conversation carries both, live and when
 * it's read back from the harness.
 */

/** How background work ended, as its notice says. */
export type BackgroundState = "completed" | "error" | "cancelled";

export interface BackgroundNotice {
  /** "shell" for a command, "subagent" for a child session. */
  kind: "shell" | "subagent";
  /** The shell id or child session the notice is about, which matches the call that started it. */
  id: string;
  state: BackgroundState;
  /** The command, or what the subagent was asked to do. */
  label: string;
  /** The output, or what the subagent said. */
  text: string;
}

const SHELL_NOTICE = /^<shell id="([^"]*)" state="([^"]*)" command="([\s\S]*?)">\n([\s\S]*)\n<\/shell>\s*$/;
const SUBAGENT_NOTICE = /^<subagent sessionID="([^"]*)" state="([^"]*)" description="([\s\S]*?)">\n([\s\S]*)\n<\/subagent>\s*$/;

function state(value: string): BackgroundState {
  return value === "error" || value === "cancelled" ? value : "completed";
}

/** The background work a message says finished, or null for anything else. */
export function parseBackgroundNotice(body: string): BackgroundNotice | null {
  const shell = SHELL_NOTICE.exec(body);
  if (shell) {
    return { kind: "shell", id: shell[1], state: state(shell[2]), label: shell[3], text: shell[4] };
  }

  const subagent = SUBAGENT_NOTICE.exec(body);
  if (subagent) {
    return { kind: "subagent", id: subagent[1], state: state(subagent[2]), label: subagent[3], text: subagent[4] };
  }

  return null;
}

/** The handle a tool call's state carries when its work went to the background, or null when it didn't. */
export function backgroundWorkId(state: unknown): string | null {
  const record = asRecord(state);
  // Only a call that has returned: an ordinary running subagent reports its child the same way.
  if (record?.background !== true) return null;
  const metadata = asRecord(record.metadata);
  if (!metadata) return null;

  for (const key of ["shellID", "sessionID"]) {
    const value = metadata[key];
    if (typeof value === "string" && value) return value;
  }

  return null;
}

/** How each piece of background work in a conversation ended, by handle; work still running isn't in it. */
export function finishedBackgroundWork(bodies: Iterable<string>): Map<string, BackgroundState> {
  const finished = new Map<string, BackgroundState>();
  for (const body of bodies) {
    const notice = parseBackgroundNotice(body);
    if (notice) finished.set(notice.id, notice.state);
  }
  return finished;
}

function asRecord(value: unknown): Record<string, unknown> | null {
  if (!value || typeof value !== "object" || Array.isArray(value)) return null;
  return value as Record<string, unknown>;
}
