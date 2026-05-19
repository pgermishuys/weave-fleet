import type { AccumulatedToolPart } from "@/lib/api-types";
import { getToolLabel } from "@/lib/tool-labels";

export interface DiffLine {
  type: "add" | "remove" | "context";
  content: string;
  oldLineNumber?: number;
  newLineNumber?: number;
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
}

const tool_output_keys = ["output", "result", "content", "error", "message", "stdout", "stderr"] as const;
const fallback_excluded_keys = new Set(["input", "status", "summary", "diff", "diffLines", "patch"]);

export function toToolCardItem(part: AccumulatedToolPart): ToolCardItem {
  const state = asRecord(part.state);
  const input = asRecord(state?.input);
  const title = getToolLabel(part.tool, input) || part.tool;

  return {
    id: part.partId,
    title,
    kind: part.tool,
    status: formatToolStatus(state?.status),
    summary: getStringValue(state?.summary),
    output: getToolOutput(state),
    diffLines: getDiffLines(state),
    initiallyCollapsed: state?.status === "completed",
  };
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
