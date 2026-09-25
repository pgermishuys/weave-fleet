import type { AccumulatedToolPart } from "@/lib/client-types";
import { apiUrl } from "@/lib/api-client";
import { backgroundWorkId, type BackgroundState } from "@/lib/background-work";
import { getToolLabel } from "@/lib/tool-labels";

export interface DiffLine {
  type: "add" | "remove" | "context";
  content: string;
  oldLineNumber?: number;
  newLineNumber?: number;
}

/** The session a sub-agent tool call started, which its row opens. */
export interface ToolCardDelegation {
  href: string;
  childSessionId: string;
  childInstanceId: string;
  parentSessionId: string;
  /** The kind of sub-agent, such as "explore"; empty when the call doesn't say. */
  agent: string;
  task: string;
  /** "pending" | "running" | "completed" | "error" | "cancelled". */
  status: string;
  /** The sub-agent has stopped on a question of its own and waits for you in its session. */
  needsInput?: boolean;
  /** The call returned and the sub-agent works on in the background; the session is free meanwhile. */
  background?: boolean;
}

/** A screenshot the agent took, which Fleet kept so the conversation can show it under the call. */
export interface ToolCardScreenshot {
  url: string;
  /** The size it was taken at, so the thumbnail keeps its shape before the image arrives. */
  width: number;
  height: number;
}

export interface ToolCardItem {
  id: string;
  title: string;
  kind?: string;
  status?: string;
  summary?: string;
  output?: string;
  diffLines?: DiffLine[];
  initiallyCollapsed?: boolean;
  preview?: string;
  isPatternTool?: boolean;
  /** The canvas a Fleet canvas tool opened or changed, which the card can bring forward. */
  canvasId?: string;
  /** Set on a sub-agent call once its session exists; the row then opens that session. */
  delegation?: ToolCardDelegation;
  screenshot?: ToolCardScreenshot;
}

/** The kind of sub-agent a call asked for: OpenCode names it `subagent_type`, OpenCode 2 `agent`. */
export function subagentKind(part: AccumulatedToolPart): string {
  const input = toolInput(part);
  for (const key of ["subagent_type", "agent"]) {
    const value = input?.[key];
    if (typeof value === "string" && value.trim()) return value.trim();
  }
  return "";
}

/** What the call asked the sub-agent to do, in its own words. */
export function subagentTask(part: AccumulatedToolPart): string {
  const description = toolInput(part)?.description;
  return typeof description === "string" ? description.trim() : "";
}

/** The tools that run an agent in a child session: OpenCode's `task`, OpenCode 2's `subagent`. */
const SUBAGENT_TOOLS = new Set(["task", "subagent"]);

/** Whether a tool call ran a subagent, so its card links to the child session. */
export function isSubagentTool(toolName: string): boolean {
  return SUBAGENT_TOOLS.has(toolName);
}

/** Fleet's browser tools; their card reads "title · address" once the page answered, or "title · size" for a shot. */
const BROWSER_TOOLS = new Set(["fleet_app_start", "fleet_browser_open", "fleet_browser_screenshot"]);

/** Tools whose card title comes from Fleet's answer: "Messaged Update documentation" for fleet_message. */
const TITLED_TOOLS = new Set([...BROWSER_TOOLS, "fleet_message"]);

const tool_output_keys = ["output", "result", "content", "error", "message", "stdout", "stderr"] as const;
const fallback_excluded_keys = new Set(["input", "status", "summary", "title", "diff", "diffLines", "patch"]);

function buildPreview(output: string | undefined, summary: string | undefined): string | undefined {
  if (output) {
    const lines = output.split("\n");
    const firstNonEmpty = lines.find((line) => line.trim());
    
    if (!firstNonEmpty) {
      return summary ? `└ ${summary}` : undefined;
    }

    const truncated = firstNonEmpty.length > 80 
      ? `${firstNonEmpty.slice(0, 77)}...` 
      : firstNonEmpty;

    const totalLines = lines.length;
    
    if (totalLines > 1) {
      return `└ ${truncated} (${totalLines} lines)`;
    }
    
    return `└ ${truncated}`;
  }

  if (summary) {
    return `└ ${summary}`;
  }

  return undefined;
}


/**
 * The parsed input a tool was called with ("filePath", "command", …), or null when it has none.
 * Exposed so other readers of the conversation (the Turns canvas) don't re-derive tool state shapes.
 */
export function toolInput(part: AccumulatedToolPart): Record<string, unknown> | null {
  return asRecord(asRecord(part.state)?.input);
}

/** The tool's raw status: "pending" | "running" | "completed" | "error", or undefined. */
export function toolStatus(part: AccumulatedToolPart): string | undefined {
  return getStringValue(asRecord(part.state)?.status)?.toLowerCase();
}

/** The diff lines a harness attached to the call, as an array; empty when it attached none. */
export function toolDiffLines(part: AccumulatedToolPart): DiffLine[] {
  return getDiffLines(asRecord(part.state));
}

/**
 * The screenshot Fleet kept for the call, from `metadata.screenshot` ({ sessionId, id, width, height }). The harness
 * keeps metadata with the call, so it's there after a reload too. The session is Fleet's, and can be the parent's:
 * a sub-agent's shot is kept under the session you started.
 */
export function toolScreenshot(part: AccumulatedToolPart): ToolCardScreenshot | undefined {
  const shot = asRecord(asRecord(asRecord(part.state)?.metadata)?.screenshot);
  const sessionId = getStringValue(shot?.sessionId);
  const id = getStringValue(shot?.id);
  const width = getNumberValue(shot?.width);
  const height = getNumberValue(shot?.height);
  if (!sessionId || !id || !width || !height) return undefined;

  return {
    url: apiUrl(`/api/sessions/${encodeURIComponent(sessionId)}/screenshots/${encodeURIComponent(id)}`),
    width,
    height,
  };
}

/** Anything on the call's state or metadata that looks like a unified diff, as text. */
export function toolDiffText(part: AccumulatedToolPart): string | undefined {
  const state = asRecord(part.state);
  const metadata = asRecord(state?.metadata);
  for (const candidate of [state?.diff, state?.patch, metadata?.diff, metadata?.patch]) {
    const text = getStringValue(candidate);
    if (text) return text;
  }
  return undefined;
}

/**
 * A tool call as its card. `finishedBackgroundWork` says how work the call moved into the background ended, by handle:
 * a backgrounded call is still running as far as the call itself goes, and only its notice says it's done.
 */
export function toToolCardItem(
  part: AccumulatedToolPart,
  finishedBackgroundWork?: ReadonlyMap<string, BackgroundState>,
): ToolCardItem {
  const state = asRecord(part.state);
  const input = asRecord(state?.input);
  const output = getToolOutput(state);
  const summary = getStringValue(state?.summary);
  const shownTitle = TITLED_TOOLS.has(part.tool) ? getStringValue(state?.title) : undefined;
  const title = shownTitle ?? (getToolLabel(part.tool, input) || part.tool);
  const canvasId = getStringValue(asRecord(state?.metadata)?.canvasId);
  const status = backgroundStatus(part, finishedBackgroundWork) ?? formatToolStatus(state?.status);

  return {
    id: part.partId,
    title,
    kind: part.tool,
    status,
    summary,
    output,
    diffLines: getDiffLines(state),
    initiallyCollapsed: state?.status !== "error",
    preview: buildPreview(output, summary),
    isPatternTool: part.tool === "glob" || part.tool === "grep",
    canvasId,
    screenshot: toolScreenshot(part),
  };
}

const BACKGROUND_STATE_TO_STATUS: Record<BackgroundState, string> = {
  completed: "Completed",
  error: "Error",
  cancelled: "Cancelled",
};

/** What a call that went to the background shows: "Background" while its work runs, then how the work ended. */
function backgroundStatus(
  part: AccumulatedToolPart,
  finished?: ReadonlyMap<string, BackgroundState>,
): string | undefined {
  const id = backgroundWorkId(part.state);
  if (!id) return undefined;
  const ended = finished?.get(id);
  return ended ? BACKGROUND_STATE_TO_STATUS[ended] : "Background";
}

function getToolOutput(state: Record<string, unknown> | null): string | undefined {
  if (!state) {
    return undefined;
  }

  for (const key of tool_output_keys) {
    if (Object.hasOwn(state, key)) {
      const value = state[key];
      if (value != null) {
        return stringifyToolValue(value);
      }
    }
  }

  if (Object.keys(state).length === 0) {
    return undefined;
  }

  const fallbackState = Object.fromEntries(
    Object.entries(state).filter(([key]) => !fallback_excluded_keys.has(key)),
  );

  return Object.keys(fallbackState).length > 0
    ? stringifyToolValue(fallbackState)
    : undefined;
}

function getDiffLines(state: Record<string, unknown> | null): DiffLine[] {
  const candidates = [state?.diffLines, state?.diff, state?.patch];

  for (const candidate of candidates) {
    const lines = normalizeDiffLines(candidate);

    if (lines.length > 0) {
      return lines;
    }
  }

  return [];
}

function normalizeDiffLines(value: unknown): DiffLine[] {
  if (!Array.isArray(value)) {
    return [];
  }

  return value.flatMap((entry) => {
    const record = asRecord(entry);
    const content = getStringValue(record?.content) ?? getStringValue(record?.text) ?? getStringValue(record?.line);
    const type = getStringValue(record?.type) ?? inferDiffType(content);

    if (!content || !type || !isDiffType(type)) {
      return [];
    }

    return [{
      type,
      content,
      oldLineNumber: getNumberValue(record?.oldLineNumber),
      newLineNumber: getNumberValue(record?.newLineNumber),
    } satisfies DiffLine];
  });
}

function inferDiffType(content: string | undefined): DiffLine["type"] | null {
  if (!content) {
    return null;
  }

  if (content.startsWith("+")) {
    return "add";
  }

  if (content.startsWith("-")) {
    return "remove";
  }

  return "context";
}

function isDiffType(value: string): value is DiffLine["type"] {
  return value === "add" || value === "remove" || value === "context";
}

function formatToolStatus(value: unknown): string {
  const status = getStringValue(value);
  if (!status) {
    return "Pending";
  }

  return status.charAt(0).toUpperCase() + status.slice(1);
}

function stringifyToolValue(value: unknown): string | undefined {
  if (typeof value === "string") {
    return value;
  }

  if (value == null) {
    return undefined;
  }

  try {
    return JSON.stringify(value, null, 2);
  } catch {
    return String(value);
  }
}

function getStringValue(value: unknown): string | undefined {
  return typeof value === "string" && value.trim() ? value : undefined;
}

function getNumberValue(value: unknown): number | undefined {
  return typeof value === "number" && Number.isFinite(value) ? value : undefined;
}

function asRecord(value: unknown): Record<string, unknown> | null {
  if (!value || typeof value !== "object" || Array.isArray(value)) {
    return null;
  }

  return value as Record<string, unknown>;
}
