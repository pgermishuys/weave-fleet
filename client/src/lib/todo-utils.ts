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

/** Reads one todo item from the server. Returns null for anything without text. */
export function normalizeTodoItem(value: unknown): TodoItem | null {
  const item = asRecord(value);
  if (!item || typeof item.content !== "string" || item.content.trim() === "") {
    return null;
  }

  return {
    content: item.content,
    status: isTodoStatus(item.status) ? item.status : "pending",
    priority: isTodoPriority(item.priority) ? item.priority : "medium",
  };
}
