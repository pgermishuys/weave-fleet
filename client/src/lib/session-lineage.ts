/**
 * Lineage: which session started which. A session is a fork of another (`forkedFromSessionId`), was started by another
 * session's agent (`spawnedBySessionId`), or is a subagent's hidden child session (`parentSessionId`, and a running work
 * item's `childSessionId`). See *Lineage* in `docs/background-work-and-lineage.md`.
 *
 * The session list nests a session's children under it, the Agents tab lists them, and the header links back to the
 * session that started this one. Everything here is pure, so the list, the tab and the header read lineage the same way.
 */
import type { SessionListItem } from "@/api/client";
import type { AccumulatedMessage } from "@/lib/client-types";
import { isWorkRunning, workFailed, type RunningWorkItem } from "@/lib/running-work";

/** How a child came from its parent: a subagent's session, a fork, or a session its agent started. */
export type LineageKind = "subagent" | "fork" | "started";

/** The lineage fields a list item carries. */
export type LineageFields = Pick<SessionListItem, "forkedFromSessionId" | "spawnedBySessionId" | "spawnKind" | "parentSessionId">;

export interface LineageLink {
  parentId: string;
  kind: LineageKind;
}

/**
 * Where a session came from, or null when the user started it. A hidden delegated child names its parent as a subagent;
 * a session can be both a fork and started by an agent (an agent forking), and then its `spawnKind` decides.
 */
export function lineageOf(item: LineageFields): LineageLink | null {
  if (item.parentSessionId) return { parentId: item.parentSessionId, kind: "subagent" };
  const fork = item.forkedFromSessionId ? { parentId: item.forkedFromSessionId, kind: "fork" as const } : null;
  const started = item.spawnedBySessionId ? { parentId: item.spawnedBySessionId, kind: "started" as const } : null;
  return item.spawnKind === "fork" ? fork ?? started : started ?? fork;
}

/** What a child's row calls its kind. */
export function lineageKindLabel(kind: LineageKind): string {
  switch (kind) {
    case "subagent": return "subagent";
    case "fork": return "fork";
    default: return "started";
  }
}

/** The words before the parent's title next to a session's title: "Started by …", "Forked from …". */
export function lineageLinkLabel(kind: LineageKind): string {
  switch (kind) {
    case "fork": return "Forked from";
    case "subagent": return "Subagent of";
    default: return "Started by";
  }
}

/** What the Agents tab says the parent did: "started this session". */
export function lineageParentNote(kind: LineageKind): string {
  switch (kind) {
    case "fork": return "forked into this session";
    case "subagent": return "delegated this session";
    default: return "started this session";
  }
}

export interface LineageChild<T> {
  item: T;
  kind: LineageKind;
}

export interface NestedLineage<T> {
  /** The sessions that show at the top level, in the order they came. */
  roots: T[];
  /** Each root's children, by its id, in the order they came. */
  childrenOf: Map<string, LineageChild<T>[]>;
}

/**
 * Nests forks and started sessions under the session they came from, when it's in the same list. A child whose parent
 * isn't there (archived, filtered out, in another project) stays at the top level. The tree is one level deep: a fork
 * of a fork sits under the first ancestor that is a root, with its own kind.
 */
export function nestLineage<T extends LineageFields & { session: { id: string } }>(items: readonly T[]): NestedLineage<T> {
  const byId = new Map(items.map((item) => [item.session.id, item]));

  function rootOf(item: T): string | null {
    const seen = new Set<string>([item.session.id]);
    let link = lineageOf(item);
    let root: string | null = null;
    while (link && byId.has(link.parentId) && !seen.has(link.parentId)) {
      root = link.parentId;
      seen.add(root);
      link = lineageOf(byId.get(root)!);
    }
    return root;
  }

  const roots: T[] = [];
  const childrenOf = new Map<string, LineageChild<T>[]>();
  for (const item of items) {
    const root = rootOf(item);
    const kind = lineageOf(item)?.kind;
    if (!root || !kind) {
      roots.push(item);
      continue;
    }
    const children = childrenOf.get(root);
    if (children) children.push({ item, kind });
    else childrenOf.set(root, [{ item, kind }]);
  }
  return { roots, childrenOf };
}

/**
 * Running subagents that have a session of their own, by the session that started them: the session list nests them
 * under it. A finished subagent drops out (the Agents tab keeps it), as does one without a session (nothing to open).
 */
export function runningSubagentsBySession(items: Iterable<RunningWorkItem>): Map<string, RunningWorkItem[]> {
  const bySession = new Map<string, RunningWorkItem[]>();
  for (const item of items) {
    if (item.kind !== "subagent" || !item.childSessionId || !isWorkRunning(item)) continue;
    const list = bySession.get(item.sessionId);
    if (list) list.push(item);
    else bySession.set(item.sessionId, [item]);
  }
  return bySession;
}

// ─── The Agents tab ─────────────────────────────────────────────────────────

/** Where a row stands: working, waiting on the user, or how it ended. */
export type AgentRowState = "running" | "waiting" | "idle" | "done" | "failed" | "stopped";

export interface AgentRow {
  key: string;
  /** A subagent or task the agent ran (from running work), or a session it forked or started. */
  kind: LineageKind | "task";
  /** The agent's name or the session's title. */
  name: string;
  /** What it was asked to do, when that's more than its name. */
  task: string | null;
  state: AgentRowState;
  /** The Fleet session it runs in, to open; null when it has none. */
  sessionId: string | null;
  work: RunningWorkItem | null;
  session: SessionListItem | null;
}

export interface AgentsLineage {
  running: AgentRow[];
  /** Sessions this one forked or started that aren't working now. */
  started: AgentRow[];
  /** Subagents and tasks that ended, newest first. */
  earlier: AgentRow[];
}

/** Only agents show in the tab: shells and monitors stay in the background strip. */
function isAgentWork(item: RunningWorkItem): boolean {
  return item.kind === "subagent" || item.kind === "task";
}

function workState(item: RunningWorkItem): AgentRowState {
  if (isWorkRunning(item)) return "running";
  if (workFailed(item)) return "failed";
  return item.status === "cancelled" || item.endedReason === "cancelled" ? "stopped" : "done";
}

/** A session row's state from the list's status. */
export function sessionAgentState(item: Pick<SessionListItem, "sessionStatus" | "activityStatus">): AgentRowState {
  if (item.sessionStatus === "waiting_input" || item.activityStatus === "waiting_input") return "waiting";
  if (item.sessionStatus === "active") return "running";
  if (item.sessionStatus === "error") return "failed";
  return "idle";
}

function workRow(item: RunningWorkItem): AgentRow {
  const task = item.label && item.label !== item.title ? item.label : null;
  return {
    key: `work:${item.id}`,
    kind: item.kind === "task" ? "task" : "subagent",
    name: item.title || item.label || "subagent",
    task,
    state: workState(item),
    sessionId: item.childSessionId,
    work: item,
    session: null,
  };
}

function sessionRow(item: SessionListItem, kind: LineageKind): AgentRow {
  return {
    key: `session:${item.session.id}`,
    kind,
    name: item.session.title?.trim() || "Untitled session",
    task: null,
    state: sessionAgentState(item),
    sessionId: item.session.id,
    work: null,
    session: item,
  };
}

/**
 * The Agents tab's sections for a session: what runs now (its subagents and tasks, and the sessions it started that are
 * working or waiting on you), the sessions it forked or started, and the subagents that ended. `work` is everything
 * Fleet said about the session's work, `sessions` the session list.
 */
export function buildAgentsLineage(
  sessionId: string,
  sessions: readonly SessionListItem[],
  work: readonly RunningWorkItem[],
): AgentsLineage {
  const agentWork = work.filter((item) => item.sessionId === sessionId && isAgentWork(item));
  const workRows = agentWork.map(workRow);
  // A subagent's session is listed through its work item, not again as a child session.
  const subagentSessions = new Set(agentWork.map((item) => item.childSessionId).filter(Boolean));

  const childRows: AgentRow[] = [];
  for (const item of sessions) {
    if (item.session.id === sessionId || subagentSessions.has(item.session.id)) continue;
    const link = lineageOf(item);
    if (link?.parentId !== sessionId || link.kind === "subagent") continue;
    childRows.push(sessionRow(item, link.kind));
  }

  const isActive = (row: AgentRow) => row.state === "running" || row.state === "waiting";
  const started = (row: AgentRow) => Date.parse(row.work?.startedAt ?? "") || 0;
  const ended = (row: AgentRow) => Date.parse(row.work?.endedAt ?? "") || started(row);

  return {
    running: [...workRows.filter(isActive), ...childRows.filter(isActive)],
    started: childRows.filter((row) => !isActive(row)),
    earlier: workRows.filter((row) => !isActive(row)).sort((a, b) => ended(b) - ended(a)),
  };
}

// ─── A selected agent's detail ──────────────────────────────────────────────

export interface AgentActivity {
  /** The first thing it was asked, in its own session. */
  asked: string | null;
  toolCalls: number;
  /** The tools it used most, most used first. */
  topTools: string[];
  tokensIn: number;
  tokensOut: number;
  /** The last thing it said, on one line. */
  latest: string | null;
  /** The model that answered last. */
  modelId: string | null;
}

function textOf(message: AccumulatedMessage): string {
  return message.parts
    .filter((part) => part.type === "text")
    .map((part) => (part as { text: string }).text)
    .join("\n")
    .trim();
}

function lastLine(text: string): string | null {
  const lines = text.split("\n").map((line) => line.trim()).filter(Boolean);
  return lines.at(-1) ?? null;
}

/** What an agent's session shows of its work: what it was asked, its tool calls and tokens, and its latest words. */
export function summarizeAgentActivity(messages: readonly AccumulatedMessage[]): AgentActivity {
  let asked: string | null = null;
  let toolCalls = 0;
  let tokensIn = 0;
  let tokensOut = 0;
  let latest: string | null = null;
  let modelId: string | null = null;
  const toolUses = new Map<string, number>();

  for (const message of messages) {
    if (message.role === "user") {
      asked ??= textOf(message).split("\n")[0]?.trim() || null;
      continue;
    }
    if (message.role !== "assistant") continue;
    tokensIn += message.tokens?.input ?? 0;
    tokensOut += message.tokens?.output ?? 0;
    if (message.modelID) modelId = message.modelID;
    for (const part of message.parts) {
      if (part.type === "tool") {
        toolCalls += 1;
        const name = toolDisplayName(part.tool);
        toolUses.set(name, (toolUses.get(name) ?? 0) + 1);
      } else if (part.type === "text" && part.text.trim()) {
        latest = lastLine(part.text) ?? latest;
      }
    }
  }

  const topTools = [...toolUses].sort((a, b) => b[1] - a[1]).slice(0, 3).map(([name]) => name);
  return { asked, toolCalls, topTools, tokensIn, tokensOut, latest, modelId };
}

/** `read` → `Read`, `mcp__fleet__fleet_page_show` → `fleet_page_show`. */
function toolDisplayName(tool: string): string {
  const name = tool.split("__").at(-1) || tool;
  return name.charAt(0).toUpperCase() + name.slice(1);
}

/** The last non-empty line of a work item's output. */
export function latestOutputLine(output: string): string | null {
  return lastLine(output);
}
