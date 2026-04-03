/**
 * Utilities for parsing and extracting todo items from `todowrite` tool calls.
 * These are pure functions with no React dependencies — safe to import anywhere.
 */

import type { AccumulatedMessage } from "@/lib/api-types";

// ─── Types ─────────────────────────────────────────────────────────────────

export interface TodoItem {
  content: string;
  status: "pending" | "in_progress" | "completed" | "cancelled";
  priority: "high" | "medium" | "low";
}

// ─── Helpers ───────────────────────────────────────────────────────────────

/** Case-insensitive match for all known todowrite tool name variants. */
export function isTodoWriteTool(toolName: string): boolean {
  const lower = toolName.toLowerCase();
  return lower === "todowrite" || lower === "todo_write";
}

/**
 * Safely parses a todowrite tool output string into a `TodoItem[]`.
 * Returns `null` if the value is missing, not valid JSON, or not a valid array.
 */
export function parseTodoOutput(output: unknown): TodoItem[] | null {
  if (typeof output !== "string" || output.trim() === "") return null;

  let parsed: unknown;
  try {
    parsed = JSON.parse(output);
  } catch {
    return null;
  }

  if (!Array.isArray(parsed)) return null;

  const items: TodoItem[] = [];
  for (const item of parsed) {
    if (
      item === null ||
      typeof item !== "object" ||
      typeof (item as Record<string, unknown>).content !== "string"
    ) {
      return null;
    }

    const raw = item as Record<string, unknown>;
    const status = raw.status;
    const priority = raw.priority;

    const validStatuses = ["pending", "in_progress", "completed", "cancelled"];
    const validPriorities = ["high", "medium", "low"];

    items.push({
      content: raw.content as string,
      status: validStatuses.includes(status as string)
        ? (status as TodoItem["status"])
        : "pending",
      priority: validPriorities.includes(priority as string)
        ? (priority as TodoItem["priority"])
        : "medium",
    });
  }

  return items;
}

/**
 * Scans all accumulated messages in reverse to find the most recent
 * `todowrite` tool part with a completed state and valid output.
 * Returns the parsed todos or `null` if none found.
 */
export function extractLatestTodos(
  messages: AccumulatedMessage[]
): TodoItem[] | null {
  for (let i = messages.length - 1; i >= 0; i--) {
    const msg = messages[i];
    for (let j = msg.parts.length - 1; j >= 0; j--) {
      const part = msg.parts[j];
      if (part.type !== "tool") continue;
      if (!isTodoWriteTool(part.tool)) continue;

      // eslint-disable-next-line @typescript-eslint/no-explicit-any
      const state = part.state as any;
      if (state?.status !== "completed") continue;

      const todos = parseTodoOutput(state?.output);
      if (todos !== null) return todos;
    }
  }
  return null;
}
