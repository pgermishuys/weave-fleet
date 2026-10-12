import { reactive, shallowReactive, watchEffect } from "vue";
import { onDomainEvent } from "@/composables/on-domain-event";
import { apiFetchOn } from "@/lib/api-client";
import { modActionSender } from "@/lib/mods/actions";
import { toolRowViews, type ToolRowView } from "@/lib/mods/points";
import type { ModAuthor, ModWireElement } from "@/lib/mods/types";
import { useMachineTarget, type MachineTarget } from "@/lib/machine-target";
import { getTool } from "@/lib/tools";

/**
 * Asks the server to draw tool rows. A session's mods say which tools they hook at which site (`/mods/sites`); a row
 * of a hooked tool asks for its tree (`/mods/render`) when it appears and when its status changes, never as its
 * output streams, and the answer is contributed to `toolRowViews`, which the desktop and phone rows already draw.
 * Rows of one call, on desktop or phone, share one request. `mod.ui` and `mods.changed` ask for fresh drawings.
 */

type Site = ToolRowView["site"];
const SITES: readonly Site[] = ["ToolUse", "ToolResult"];

/** The contract's limits on what a mod is sent. */
export const MAX_INPUT_BYTES = 64 * 1024;
export const MAX_OUTPUT_CHARS = 262_144;
const MAX_IN_FLIGHT = 4;

interface SiteHook {
  site: Site;
  /** Canonical tool names; null hooks every tool. */
  tools: string[] | null;
}

interface RenderResponse {
  tree: ModWireElement | null;
  mods: ModAuthor[];
}

/** What a row is: the part that decides when to ask (read in an effect) and the part sent (read only when asking). */
export interface ModToolRow {
  sessionId?: string;
  callId?: string;
  /** The name the harness sent. */
  rawTool: string;
  /** Fleet's status text ("Completed", "Stopped", …) or the harness's ("completed"). */
  status: string;
  read: () => { input?: Record<string, unknown> | null; output?: string; title: string; durationMs?: number };
}

/** A pane a mod declares in mod.json, listed in the session's "+" menu; `off` when its mod is turned off. */
export interface DeclaredModPane {
  mod: string;
  id: string;
  title: string;
  icon?: string | null;
  draft: boolean;
  off: boolean;
}

/** The panes the session's mods declare, as the server last said. */
const declaredPanes = shallowReactive<Record<string, DeclaredModPane[]>>({});

/** A session's hooks as the server last said; null while unknown or when it has none (Mods off). */
const hooks = shallowReactive<Record<string, SiteHook[] | null>>({});
/** Bumped to make a session's rows (or one call's) ask again. */
const sessionGen = reactive<Record<string, number>>({});
const callGen = reactive<Record<string, number>>({});
/** The key each owner last asked for, and the answers by key (shared by every row of the call). */
const wanted = new Map<string, string>();
const answers = new Map<string, Promise<RenderResponse | null>>();
const ownersBySession = new Map<string, Set<string>>();
const listening = new Set<string>();
const queue: Array<() => void> = [];
let inFlight = 0;

/** Forgets everything; for tests. */
export function resetModRenders(): void {
  for (const store of [hooks, sessionGen, callGen, declaredPanes] as Array<Record<string, unknown>>) {
    for (const key of Object.keys(store)) delete store[key];
  }
  wanted.clear();
  answers.clear();
  ownersBySession.clear();
  listening.clear();
  queue.length = 0;
  inFlight = 0;
}

const ownerOf = (site: Site, sessionId: string, callId: string) => `mod-row:${site}:${sessionId}:${callId}`;

function schedule<T>(job: () => Promise<T>): Promise<T> {
  return new Promise<T>((resolve, reject) => {
    const run = () => {
      inFlight += 1;
      job()
        .then(resolve, reject)
        .finally(() => {
          inFlight -= 1;
          queue.shift()?.();
        });
    };
    if (inFlight < MAX_IN_FLIGHT) run();
    else queue.push(run);
  });
}

function canonicalStatus(status: string): string {
  const lower = status.toLowerCase();
  if (lower === "stopped") return "cancelled";
  if (lower === "background") return "running";
  return ["pending", "running", "completed", "error", "cancelled"].includes(lower) ? lower : "pending";
}

/** The props a mod is sent for a row, within the contract's limits. */
export function renderProps(row: ModToolRow) {
  const { input, output, title, durationMs } = row.read();
  const tool = getTool(row.rawTool);
  const inputTruncated = new TextEncoder().encode(JSON.stringify(input ?? {})).length > MAX_INPUT_BYTES;
  const text = output ?? "";
  return {
    tool: tool.name,
    rawTool: row.rawTool,
    category: tool.category,
    status: canonicalStatus(row.status),
    title,
    input: inputTruncated ? {} : (input ?? {}),
    inputTruncated,
    output: text.length > MAX_OUTPUT_CHARS ? text.slice(-MAX_OUTPUT_CHARS) : text,
    outputTruncated: text.length > MAX_OUTPUT_CHARS,
    ...(durationMs === undefined ? {} : { durationMs }),
  };
}

async function fetchHooks(machine: MachineTarget, sessionId: string): Promise<SiteHook[] | null> {
  try {
    const response = await apiFetchOn(machine.connection, `/api/sessions/${encodeURIComponent(sessionId)}/mods/sites`);
    // 404 is Mods off: no sites, and no asking again until `mods.changed`.
    if (!response.ok) {
      declaredPanes[sessionId] = [];
      return null;
    }
    const body = (await response.json()) as { sites?: Array<{ site: Site; tools: string[] | null }>; panes?: DeclaredModPane[] };
    declaredPanes[sessionId] = body.panes ?? [];
    return (body.sites ?? []).map((hook) => ({ site: hook.site, tools: hook.tools?.map((tool) => getTool(tool).name) ?? null }));
  } catch {
    return null;
  }
}

async function loadHooks(machine: MachineTarget, sessionId: string): Promise<void> {
  const loaded = await fetchHooks(machine, sessionId);
  hooks[sessionId] = loaded;
  if (!loaded) forgetViews(sessionId);
  // A new version may draw differently.
  sessionGen[sessionId] = (sessionGen[sessionId] ?? 0) + 1;
}

function forgetViews(sessionId: string): void {
  for (const owner of ownersBySession.get(sessionId) ?? []) {
    toolRowViews.removeByOwner(owner);
    wanted.delete(owner);
  }
}

/** `mod.ui`: make the session's rows (or one call's) ask again. */
function invalidate(sessionId: string | null, requestId: string | null): void {
  for (const id of sessionId ? [sessionId] : Object.keys(hooks)) {
    if (requestId) callGen[`${id}:${requestId}`] = (callGen[`${id}:${requestId}`] ?? 0) + 1;
    else sessionGen[id] = (sessionGen[id] ?? 0) + 1;
  }
}

/** Listens for the machine's `mods.changed` and `mod.ui`, and the session's `mod.ui`, once each. */
function listen(machine: MachineTarget, sessionId: string): void {
  if (!listening.has(machine.key)) {
    listening.add(machine.key);
    const reloadAll = () => {
      for (const id of Object.keys(hooks)) void loadHooks(machine, id);
    };
    onDomainEvent(machine, "sessions", "mods.changed", reloadAll);
    // With no session it's every session. Fleet's own (no mod) follows the host loading or dropping mods, which changes
    // which sites they hook, so ask for the sites again. A mod's own redraw can't change its hooks: only draw again.
    onDomainEvent(machine, "sessions", "mod.ui", ({ payload }) => {
      if (payload.sessionId || payload.mod) invalidate(payload.sessionId, payload.requestId);
      else reloadAll();
    });
  }
  const topic = `${machine.key}:session:${sessionId}`;
  if (!listening.has(topic)) {
    listening.add(topic);
    onDomainEvent(machine, `session:${sessionId}`, "mod.ui", ({ payload }) => invalidate(sessionId, payload.requestId));
  }
}

function apply(machine: MachineTarget, owner: string, view: Omit<ToolRowView, "tree" | "mods" | "onAction">, response: RenderResponse): void {
  toolRowViews.removeByOwner(owner);
  // No mod draws: Fleet's own row. (A mod that draws nothing comes back with its name and a null tree.)
  if (response.tree === null && response.mods.length === 0) return;
  const onAction = modActionSender(machine, view.sessionId, () => invalidate(view.sessionId, view.callId));
  toolRowViews.contribute(owner, [{ ...view, tree: response.tree, mods: response.mods, onAction }]);
}

function request(machine: MachineTarget, row: ModToolRow, site: Site, sessionId: string, callId: string): void {
  const status = canonicalStatus(row.status);
  const tool = getTool(row.rawTool).name;
  const owner = ownerOf(site, sessionId, callId);
  const key = [sessionId, callId, site, status, sessionGen[sessionId] ?? 0, callGen[`${sessionId}:${callId}`] ?? 0].join("\0");
  if (wanted.get(owner) === key) return;
  wanted.set(owner, key);
  let owners = ownersBySession.get(sessionId);
  if (!owners) ownersBySession.set(sessionId, (owners = new Set()));
  owners.add(owner);

  let answer = answers.get(key);
  if (!answer) {
    answer = schedule(async () => {
      // Off the effect that asked: reading the row's output here would make every streamed chunk ask again.
      await null;
      try {
        const response = await apiFetchOn(machine.connection, `/api/sessions/${encodeURIComponent(sessionId)}/mods/render`, {
          method: "POST",
          headers: { "Content-Type": "application/json" },
          body: JSON.stringify({ site, requestId: callId, props: renderProps(row) }),
        });
        return response.ok ? ((await response.json()) as RenderResponse) : null;
      } catch {
        return null;
      }
    });
    answers.set(key, answer);
  }
  void answer.then((response) => {
    // A newer ask (another status, a `mod.ui`) has taken this one's place.
    if (response && wanted.get(owner) === key) apply(machine, owner, { site, sessionId, callId, tool }, response);
  });
}

function sync(machine: MachineTarget, row: ModToolRow): void {
  const { sessionId, callId } = row;
  if (!sessionId || !callId) return;
  listen(machine, sessionId);
  if (!(sessionId in hooks)) {
    hooks[sessionId] = null;
    void loadHooks(machine, sessionId);
    return;
  }
  const tool = getTool(row.rawTool).name;
  for (const site of SITES) {
    const hooked = hooks[sessionId]?.some((hook) => hook.site === site && (hook.tools === null || hook.tools.includes(tool)));
    if (hooked) {
      request(machine, row, site, sessionId, callId);
    } else if (wanted.delete(ownerOf(site, sessionId, callId))) {
      toolRowViews.removeByOwner(ownerOf(site, sessionId, callId));
    }
  }
}

/** Starts listening for the session's mod events and loads its hooks and declared panes once. */
export function watchModSession(machine: MachineTarget, sessionId: string): void {
  listen(machine, sessionId);
  if (!(sessionId in hooks)) {
    hooks[sessionId] = null;
    void loadHooks(machine, sessionId);
  }
}

/** The panes the session's mods declare (reactive). */
export function declaredModPanes(sessionId: string): readonly DeclaredModPane[] {
  return declaredPanes[sessionId] ?? [];
}

/** Draws the rows with mods' trees: asks for each row of a hooked tool and keeps `toolRowViews` in step. */
export function useModToolRows(rows: () => readonly ModToolRow[]): void {
  const machine = useMachineTarget();
  watchEffect(() => {
    for (const row of rows()) sync(machine, row);
  });
}
