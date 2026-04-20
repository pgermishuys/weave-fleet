import type { AccumulatedMessage } from "@/lib/api-types";

const TODO_STATUSES = ["pending", "in_progress", "completed", "cancelled"] as const;
const TODO_PRIORITIES = ["high", "medium", "low"] as const;

export interface TodoItem {
  content: string;
  status: (typeof TODO_STATUSES)[number];
  priority: (typeof TODO_PRIORITIES)[number];
}

function asRecord(value: unknown): Record<string, unknown> | null {
  if (value === null || typeof value !== "object" || Array.isArray(value)) {
    return null;
  }

  return value as Record<string, unknown>;
}

function isTodoStatus(value: unknown): value is TodoItem["status"] {
  return typeof value === "string" && TODO_STATUSES.includes(value as TodoItem["status"]);
}

function isTodoPriority(value: unknown): value is TodoItem["priority"] {
  return typeof value === "string" && TODO_PRIORITIES.includes(value as TodoItem["priority"]);
}

function normalizeTodoItem(value: unknown): TodoItem | null {
  const item = asRecord(value);
  if (!item || typeof item.content !== "string") {
    return null;
  }

  return {
    content: item.content,
    status: isTodoStatus(item.status) ? item.status : "pending",
    priority: isTodoPriority(item.priority) ? item.priority : "medium",
  };
}

function normalizeTodoList(value: unknown): TodoItem[] | null {
  if (!Array.isArray(value)) {
    return null;
  }

  const todos: TodoItem[] = [];

  for (const item of value) {
    const normalized = normalizeTodoItem(item);
    if (!normalized) {
      return null;
    }

    todos.push(normalized);
  }

  return todos;
}

function getToolOutput(state: unknown): unknown {
  const record = asRecord(state);
  if (!record) {
    return state;
  }

  if ("output" in record) {
    return record.output;
  }

  return state;
}

export function isTodoWriteTool(toolName: string): boolean {
  const lowerName = toolName.toLowerCase();
  return lowerName === "todowrite" || lowerName === "todo_write";
}

export function parseTodoOutput(output: unknown): TodoItem[] | null {
  if (Array.isArray(output)) {
    return normalizeTodoList(output);
  }

  if (typeof output !== "string") {
    return null;
  }

  const trimmedOutput = output.trim();
  if (!trimmedOutput) {
    return null;
  }

  try {
    return normalizeTodoList(JSON.parse(trimmedOutput));
  } catch {
    return null;
  }
}

export function extractLatestTodos(messages: readonly AccumulatedMessage[]): TodoItem[] {
  for (let messageIndex = messages.length - 1; messageIndex >= 0; messageIndex -= 1) {
    const message = messages[messageIndex];

    for (let partIndex = message.parts.length - 1; partIndex >= 0; partIndex -= 1) {
      const part = message.parts[partIndex];
      if (part.type !== "tool" || !isTodoWriteTool(part.tool)) {
        continue;
      }

      const state = asRecord(part.state);
      if (typeof state?.status === "string" && state.status !== "completed") {
        continue;
      }

      const todos = parseTodoOutput(getToolOutput(part.state));
      if (todos) {
        return todos;
      }
    }
  }

  return [];
}
