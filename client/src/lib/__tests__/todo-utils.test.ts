import { isTodoWriteTool, parseTodoOutput, extractLatestTodos } from "@/lib/todo-utils";
import type { AccumulatedMessage, AccumulatedToolPart, AccumulatedTextPart } from "@/lib/api-types";

// ─── Helpers ─────────────────────────────────────────────────────────────────

function makeMessage(overrides: Partial<AccumulatedMessage> = {}): AccumulatedMessage {
  return {
    messageId: "msg-1",
    sessionId: "sess-1",
    role: "assistant",
    parts: [],
    ...overrides,
  };
}

function makeToolPart(overrides: Partial<AccumulatedToolPart> = {}): AccumulatedToolPart {
  return {
    partId: "part-1",
    type: "tool",
    tool: "todowrite",
    callId: "call-1",
    state: { status: "completed", output: "[]" },
    ...overrides,
  };
}

function makeTextPart(overrides: Partial<AccumulatedTextPart> = {}): AccumulatedTextPart {
  return {
    partId: "text-part-1",
    type: "text",
    text: "hello",
    ...overrides,
  };
}

function makeTodoJson(
  items: Array<{ content?: unknown; status?: unknown; priority?: unknown }>
): string {
  return JSON.stringify(items);
}

// ─── isTodoWriteTool ──────────────────────────────────────────────────────────

describe("isTodoWriteTool", () => {
  it('returns true for "todowrite"', () => {
    expect(isTodoWriteTool("todowrite")).toBe(true);
  });

  it('returns true for "todo_write"', () => {
    expect(isTodoWriteTool("todo_write")).toBe(true);
  });

  it('returns true for "TodoWrite" (mixed case)', () => {
    expect(isTodoWriteTool("TodoWrite")).toBe(true);
  });

  it('returns true for "TODO_WRITE" (upper case)', () => {
    expect(isTodoWriteTool("TODO_WRITE")).toBe(true);
  });

  it('returns true for "Todo_Write"', () => {
    expect(isTodoWriteTool("Todo_Write")).toBe(true);
  });

  it('returns false for "other"', () => {
    expect(isTodoWriteTool("other")).toBe(false);
  });

  it('returns false for empty string', () => {
    expect(isTodoWriteTool("")).toBe(false);
  });

  it('returns false for "todowriter" (substring match should fail)', () => {
    expect(isTodoWriteTool("todowriter")).toBe(false);
  });
});

// ─── parseTodoOutput ──────────────────────────────────────────────────────────

describe("parseTodoOutput", () => {
  it("returns parsed TodoItem[] for valid JSON array with all fields", () => {
    const input = makeTodoJson([
      { content: "Task A", status: "pending", priority: "high" },
    ]);
    const result = parseTodoOutput(input);
    expect(result).toEqual([{ content: "Task A", status: "pending", priority: "high" }]);
  });

  it("returns null for null input", () => {
    expect(parseTodoOutput(null)).toBeNull();
  });

  it("returns null for undefined input", () => {
    expect(parseTodoOutput(undefined)).toBeNull();
  });

  it("returns null for empty string", () => {
    expect(parseTodoOutput("")).toBeNull();
  });

  it("returns null for number input", () => {
    expect(parseTodoOutput(42)).toBeNull();
  });

  it("returns null for object input (non-string)", () => {
    expect(parseTodoOutput({ content: "Task" })).toBeNull();
  });

  it("returns null for invalid JSON string", () => {
    expect(parseTodoOutput("not valid json {{{")).toBeNull();
  });

  it("returns null for JSON that is an object (not array)", () => {
    expect(parseTodoOutput('{"content": "Task"}')).toBeNull();
  });

  it("returns null for JSON that is a number", () => {
    expect(parseTodoOutput("42")).toBeNull();
  });

  it("returns null for array with a null element", () => {
    expect(parseTodoOutput("[null]")).toBeNull();
  });

  it("returns null for array with a non-object element (string)", () => {
    expect(parseTodoOutput('["task string"]')).toBeNull();
  });

  it("returns null for array item missing content field", () => {
    const input = makeTodoJson([{ status: "pending", priority: "high" }]);
    expect(parseTodoOutput(input)).toBeNull();
  });

  it("defaults status to 'pending' for items with missing status", () => {
    const input = makeTodoJson([{ content: "Task A", priority: "high" }]);
    const result = parseTodoOutput(input);
    expect(result).not.toBeNull();
    expect(result![0].status).toBe("pending");
  });

  it("defaults status to 'pending' for items with invalid status string", () => {
    const input = makeTodoJson([{ content: "Task A", status: "unknown_status", priority: "low" }]);
    const result = parseTodoOutput(input);
    expect(result).not.toBeNull();
    expect(result![0].status).toBe("pending");
  });

  it("defaults priority to 'medium' for items with missing priority", () => {
    const input = makeTodoJson([{ content: "Task A", status: "completed" }]);
    const result = parseTodoOutput(input);
    expect(result).not.toBeNull();
    expect(result![0].priority).toBe("medium");
  });

  it("defaults priority to 'medium' for items with invalid priority string", () => {
    const input = makeTodoJson([{ content: "Task A", status: "in_progress", priority: "urgent" }]);
    const result = parseTodoOutput(input);
    expect(result).not.toBeNull();
    expect(result![0].priority).toBe("medium");
  });

  it("returns multiple items with mixed valid fields", () => {
    const input = makeTodoJson([
      { content: "Task 1", status: "pending", priority: "high" },
      { content: "Task 2", status: "completed", priority: "low" },
      { content: "Task 3", status: "invalid", priority: "invalid" },
    ]);
    const result = parseTodoOutput(input);
    expect(result).not.toBeNull();
    expect(result).toHaveLength(3);
    expect(result![0]).toEqual({ content: "Task 1", status: "pending", priority: "high" });
    expect(result![1]).toEqual({ content: "Task 2", status: "completed", priority: "low" });
    expect(result![2]).toEqual({ content: "Task 3", status: "pending", priority: "medium" });
  });

  it("returns a single valid item with all correct fields", () => {
    const input = makeTodoJson([{ content: "Do the thing", status: "in_progress", priority: "medium" }]);
    const result = parseTodoOutput(input);
    expect(result).toEqual([{ content: "Do the thing", status: "in_progress", priority: "medium" }]);
  });

  it("accepts items with empty content string", () => {
    const input = makeTodoJson([{ content: "", status: "pending", priority: "low" }]);
    const result = parseTodoOutput(input);
    expect(result).not.toBeNull();
    expect(result![0].content).toBe("");
  });

  it("returns empty array for an empty JSON array", () => {
    const result = parseTodoOutput("[]");
    expect(result).toEqual([]);
  });

  it("preserves all valid status values", () => {
    const statuses = ["pending", "in_progress", "completed", "cancelled"] as const;
    for (const status of statuses) {
      const input = makeTodoJson([{ content: "Task", status, priority: "high" }]);
      const result = parseTodoOutput(input);
      expect(result).not.toBeNull();
      expect(result![0].status).toBe(status);
    }
  });

  it("preserves all valid priority values", () => {
    const priorities = ["high", "medium", "low"] as const;
    for (const priority of priorities) {
      const input = makeTodoJson([{ content: "Task", status: "pending", priority }]);
      const result = parseTodoOutput(input);
      expect(result).not.toBeNull();
      expect(result![0].priority).toBe(priority);
    }
  });
});

// ─── extractLatestTodos ───────────────────────────────────────────────────────

describe("extractLatestTodos", () => {
  it("returns null for an empty messages array", () => {
    expect(extractLatestTodos([])).toBeNull();
  });

  it("returns null for messages with no tool parts", () => {
    const messages = [
      makeMessage({
        parts: [makeTextPart()],
      }),
    ];
    expect(extractLatestTodos(messages)).toBeNull();
  });

  it("returns null for messages with non-todowrite tool parts", () => {
    const messages = [
      makeMessage({
        parts: [
          makeToolPart({
            tool: "bash",
            state: { status: "completed", output: JSON.stringify([{ content: "Task", status: "pending", priority: "high" }]) },
          }),
        ],
      }),
    ];
    expect(extractLatestTodos(messages)).toBeNull();
  });

  it("returns parsed todos for a single message with a completed todowrite part", () => {
    const todos = [{ content: "Task 1", status: "pending", priority: "high" }];
    const messages = [
      makeMessage({
        parts: [
          makeToolPart({
            tool: "todowrite",
            state: { status: "completed", output: JSON.stringify(todos) },
          }),
        ],
      }),
    ];
    const result = extractLatestTodos(messages);
    expect(result).toEqual(todos);
  });

  it("returns todos from the latest (last) message when multiple messages have todowrite parts", () => {
    const earlierTodos = [{ content: "Earlier task", status: "pending", priority: "low" }];
    const laterTodos = [{ content: "Later task", status: "completed", priority: "high" }];

    const messages = [
      makeMessage({
        messageId: "msg-1",
        parts: [
          makeToolPart({
            partId: "part-1",
            tool: "todowrite",
            state: { status: "completed", output: JSON.stringify(earlierTodos) },
          }),
        ],
      }),
      makeMessage({
        messageId: "msg-2",
        parts: [
          makeToolPart({
            partId: "part-2",
            tool: "todowrite",
            state: { status: "completed", output: JSON.stringify(laterTodos) },
          }),
        ],
      }),
    ];

    const result = extractLatestTodos(messages);
    expect(result).toEqual(laterTodos);
  });

  it("returns todos from the last part within a message when multiple todowrite parts exist", () => {
    const firstTodos = [{ content: "First todos", status: "pending", priority: "low" }];
    const lastTodos = [{ content: "Last todos", status: "in_progress", priority: "medium" }];

    const messages = [
      makeMessage({
        parts: [
          makeToolPart({
            partId: "part-1",
            tool: "todowrite",
            state: { status: "completed", output: JSON.stringify(firstTodos) },
          }),
          makeToolPart({
            partId: "part-2",
            tool: "todowrite",
            state: { status: "completed", output: JSON.stringify(lastTodos) },
          }),
        ],
      }),
    ];

    const result = extractLatestTodos(messages);
    expect(result).toEqual(lastTodos);
  });

  it("skips todowrite parts whose state.status is not 'completed'", () => {
    const messages = [
      makeMessage({
        parts: [
          makeToolPart({
            tool: "todowrite",
            state: { status: "running", output: JSON.stringify([{ content: "Task", status: "pending", priority: "high" }]) },
          }),
        ],
      }),
    ];
    expect(extractLatestTodos(messages)).toBeNull();
  });

  it("skips todowrite parts with invalid output and continues searching backwards", () => {
    const validTodos = [{ content: "Valid task", status: "pending", priority: "medium" }];

    const messages = [
      makeMessage({
        messageId: "msg-1",
        parts: [
          makeToolPart({
            partId: "part-valid",
            tool: "todowrite",
            state: { status: "completed", output: JSON.stringify(validTodos) },
          }),
        ],
      }),
      makeMessage({
        messageId: "msg-2",
        parts: [
          makeToolPart({
            partId: "part-invalid",
            tool: "todowrite",
            state: { status: "completed", output: "not valid json" },
          }),
        ],
      }),
    ];

    const result = extractLatestTodos(messages);
    expect(result).toEqual(validTodos);
  });

  it("returns the earlier valid todos when the last message has invalid output", () => {
    const validTodos = [{ content: "Older task", status: "cancelled", priority: "low" }];

    const messages = [
      makeMessage({
        messageId: "msg-earlier",
        parts: [
          makeToolPart({
            partId: "part-good",
            tool: "todowrite",
            state: { status: "completed", output: JSON.stringify(validTodos) },
          }),
        ],
      }),
      makeMessage({
        messageId: "msg-later",
        parts: [
          makeToolPart({
            partId: "part-bad",
            tool: "todowrite",
            state: { status: "completed", output: null },
          }),
        ],
      }),
    ];

    const result = extractLatestTodos(messages);
    expect(result).toEqual(validTodos);
  });

  it("returns null when todowrite part has a pending state (not completed)", () => {
    const messages = [
      makeMessage({
        parts: [
          makeToolPart({
            tool: "todowrite",
            state: { status: "pending" },
          }),
        ],
      }),
    ];
    expect(extractLatestTodos(messages)).toBeNull();
  });

  it("returns null when all todowrite parts have invalid outputs", () => {
    const messages = [
      makeMessage({
        messageId: "msg-1",
        parts: [
          makeToolPart({
            partId: "part-1",
            tool: "todowrite",
            state: { status: "completed", output: "{}" },
          }),
        ],
      }),
      makeMessage({
        messageId: "msg-2",
        parts: [
          makeToolPart({
            partId: "part-2",
            tool: "todowrite",
            state: { status: "completed", output: "42" },
          }),
        ],
      }),
    ];
    expect(extractLatestTodos(messages)).toBeNull();
  });

  it("handles mixed part types — skips text parts, picks up todowrite", () => {
    const todos = [{ content: "Mixed message task", status: "in_progress", priority: "high" }];

    const messages = [
      makeMessage({
        parts: [
          makeTextPart({ partId: "text-1", text: "Some text" }),
          makeToolPart({
            partId: "tool-bash",
            tool: "bash",
            state: { status: "completed", output: "echo done" },
          }),
          makeToolPart({
            partId: "tool-todo",
            tool: "todo_write",
            state: { status: "completed", output: JSON.stringify(todos) },
          }),
        ],
      }),
    ];

    const result = extractLatestTodos(messages);
    expect(result).toEqual(todos);
  });
});
