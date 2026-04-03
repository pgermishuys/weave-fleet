/**
 * Unit tests for `useActivityFilter` logic.
 *
 * The vitest environment is "node" (no jsdom). We test the exported pure
 * helper functions that power the hook's filtering logic, plus verify the
 * hook module's exports exist at the right shape.
 */

import { describe, it, expect } from "vitest";
import {
  assistantPassesTypeFilter,
  getPartSearchableText,
  partMatchesQuery,
} from "@/hooks/use-activity-filter";
import type { AccumulatedMessage, AccumulatedPart } from "@/lib/api-types";

// ─── Test Helpers ─────────────────────────────────────────────────────────────

function makeTextPart(partId: string, text: string): AccumulatedPart {
  return { partId, type: "text", text };
}

function makeToolPart(
  partId: string,
  tool: string,
  output?: string
): AccumulatedPart {
  return {
    partId,
    type: "tool",
    tool,
    callId: partId,
    state: output !== undefined ? { output } : {},
  };
}

function makeMessage(
  overrides: Partial<AccumulatedMessage> & { messageId: string }
): AccumulatedMessage {
  return {
    sessionId: "session-1",
    role: "user",
    parts: [],
    ...overrides,
  };
}

// ─── getPartSearchableText ────────────────────────────────────────────────────

describe("getPartSearchableText", () => {
  it("returns text content for text parts", () => {
    const part = makeTextPart("p1", "Hello world");
    expect(getPartSearchableText(part)).toBe("Hello world");
  });

  it("returns empty string for empty text part", () => {
    const part = makeTextPart("p1", "");
    expect(getPartSearchableText(part)).toBe("");
  });

  it("includes tool name in searchable text for tool parts", () => {
    const part = makeToolPart("p2", "bash", "some output");
    const text = getPartSearchableText(part);
    expect(text).toContain("bash");
  });

  it("includes tool output in searchable text for tool parts", () => {
    const part = makeToolPart("p2", "bash", "some output text");
    const text = getPartSearchableText(part);
    expect(text).toContain("some output text");
  });

  it("handles tool part with no output gracefully", () => {
    const part = makeToolPart("p3", "read");
    const text = getPartSearchableText(part);
    expect(text).toContain("read");
    // Should not throw even without output
  });

  it("handles tool part with non-string output gracefully", () => {
    const part: AccumulatedPart = {
      partId: "p4",
      type: "tool",
      tool: "myTool",
      callId: "c4",
      state: { output: 42 }, // non-string output
    };
    const text = getPartSearchableText(part);
    expect(text).toContain("myTool");
    // non-string output should not be included (returns empty string)
    expect(text).not.toContain("42");
  });
});

// ─── partMatchesQuery ─────────────────────────────────────────────────────────

describe("partMatchesQuery", () => {
  it("matches text part content (case-insensitive — callers pass lowercased query)", () => {
    const part = makeTextPart("p1", "Hello World");
    // partMatchesQuery expects a pre-lowercased query (matches hook internals)
    expect(partMatchesQuery(part, "hello")).toBe(true);
    expect(partMatchesQuery(part, "world")).toBe(true);
  });

  it("does not match when query is absent in text", () => {
    const part = makeTextPart("p1", "Hello World");
    expect(partMatchesQuery(part, "xyz")).toBe(false);
  });

  it("matches tool name (case-insensitive)", () => {
    const part = makeToolPart("p2", "Bash");
    expect(partMatchesQuery(part, "bash")).toBe(true);
  });

  it("matches tool output content", () => {
    const part = makeToolPart("p3", "read", "file contents here");
    expect(partMatchesQuery(part, "file contents")).toBe(true);
  });

  it("returns false when neither tool name nor output matches", () => {
    const part = makeToolPart("p4", "bash", "hello");
    expect(partMatchesQuery(part, "xyz")).toBe(false);
  });
});

// ─── assistantPassesTypeFilter ────────────────────────────────────────────────

describe("assistantPassesTypeFilter", () => {
  it("shows assistant text message when 'assistant' is in filter", () => {
    const msg = makeMessage({
      messageId: "m1",
      role: "assistant",
      parts: [makeTextPart("p1", "hi")],
    });
    expect(
      assistantPassesTypeFilter(msg, new Set(["assistant"]))
    ).toBe(true);
  });

  it("hides assistant text message when only 'tool' is in filter", () => {
    const msg = makeMessage({
      messageId: "m1",
      role: "assistant",
      parts: [makeTextPart("p1", "hi")],
    });
    expect(
      assistantPassesTypeFilter(msg, new Set(["tool"]))
    ).toBe(false);
  });

  it("shows tool-only assistant message when 'tool' is in filter", () => {
    const msg = makeMessage({
      messageId: "m2",
      role: "assistant",
      parts: [makeToolPart("p2", "bash")],
    });
    expect(
      assistantPassesTypeFilter(msg, new Set(["tool"]))
    ).toBe(true);
  });

  it("hides tool-only assistant message when only 'assistant' is in filter", () => {
    const msg = makeMessage({
      messageId: "m2",
      role: "assistant",
      parts: [makeToolPart("p2", "bash")],
    });
    expect(
      assistantPassesTypeFilter(msg, new Set(["assistant"]))
    ).toBe(false);
  });

  it("shows mixed (text+tool) message when either 'assistant' or 'tool' is in filter", () => {
    const msg = makeMessage({
      messageId: "m3",
      role: "assistant",
      parts: [makeTextPart("p1", "hi"), makeToolPart("p2", "bash")],
    });
    expect(
      assistantPassesTypeFilter(msg, new Set(["assistant"]))
    ).toBe(true);
    expect(
      assistantPassesTypeFilter(msg, new Set(["tool"]))
    ).toBe(true);
    expect(
      assistantPassesTypeFilter(msg, new Set(["assistant", "tool"]))
    ).toBe(true);
  });

  it("shows all when default filter (all three types) is active", () => {
    const textMsg = makeMessage({
      messageId: "m1",
      role: "assistant",
      parts: [makeTextPart("p1", "hi")],
    });
    const toolMsg = makeMessage({
      messageId: "m2",
      role: "assistant",
      parts: [makeToolPart("p2", "bash")],
    });
    const fullFilter = new Set<"user" | "assistant" | "tool">([
      "user",
      "assistant",
      "tool",
    ]);
    expect(assistantPassesTypeFilter(textMsg, fullFilter)).toBe(true);
    expect(assistantPassesTypeFilter(toolMsg, fullFilter)).toBe(true);
  });
});

// ─── Hook module shape ────────────────────────────────────────────────────────

describe("useActivityFilter module exports", () => {
  it("exports useActivityFilter as a function", async () => {
    const mod = await import("@/hooks/use-activity-filter");
    expect(typeof mod.useActivityFilter).toBe("function");
  });

  it("exports assistantPassesTypeFilter as a function", async () => {
    const mod = await import("@/hooks/use-activity-filter");
    expect(typeof mod.assistantPassesTypeFilter).toBe("function");
  });

  it("exports partMatchesQuery as a function", async () => {
    const mod = await import("@/hooks/use-activity-filter");
    expect(typeof mod.partMatchesQuery).toBe("function");
  });

  it("exports getPartSearchableText as a function", async () => {
    const mod = await import("@/hooks/use-activity-filter");
    expect(typeof mod.getPartSearchableText).toBe("function");
  });
});

// ─── Filtering Logic (integration-style, pure) ────────────────────────────────
// These tests replicate what the hook's useMemo would compute, verifying
// the logic works correctly end-to-end with the helper functions.

describe("filtering logic (pure)", () => {
  const messages: AccumulatedMessage[] = [
    makeMessage({
      messageId: "user-1",
      role: "user",
      agent: "orchestrator",
      parts: [makeTextPart("p1", "please run the tests")],
    }),
    makeMessage({
      messageId: "assistant-text-1",
      role: "assistant",
      agent: "orchestrator",
      parts: [makeTextPart("p2", "Running tests now")],
    }),
    makeMessage({
      messageId: "assistant-tool-1",
      role: "assistant",
      agent: "worker",
      parts: [makeToolPart("p3", "bash", "test output: PASSED")],
    }),
    makeMessage({
      messageId: "assistant-mixed-1",
      role: "assistant",
      agent: "worker",
      parts: [
        makeTextPart("p4", "Here are the results"),
        makeToolPart("p5", "read", "file content"),
      ],
    }),
    makeMessage({
      messageId: "user-2",
      role: "user",
      agent: "worker",
      parts: [makeTextPart("p6", "what about linting?")],
    }),
  ];

  function applyFilters(
    msgs: AccumulatedMessage[],
    opts: {
      searchQuery?: string;
      typeFilter?: Set<"user" | "assistant" | "tool">;
      agentFilter?: string | null;
    }
  ): AccumulatedMessage[] {
    const lowerQuery = (opts.searchQuery ?? "").trim().toLowerCase();
    const typeFilter =
      opts.typeFilter ?? new Set<"user" | "assistant" | "tool">(["user", "assistant", "tool"]);
    const agentFilter = opts.agentFilter ?? null;

    return msgs.filter((message) => {
      if (message.role === "user") {
        if (!typeFilter.has("user")) return false;
      } else {
        if (!assistantPassesTypeFilter(message, typeFilter)) return false;
      }
      if (agentFilter !== null && message.agent !== agentFilter) return false;
      if (lowerQuery) {
        const anyPartMatches = message.parts.some((part) =>
          partMatchesQuery(part, lowerQuery)
        );
        if (!anyPartMatches) return false;
      }
      return true;
    });
  }

  it("returns all messages when no filters active", () => {
    const result = applyFilters(messages, {});
    expect(result).toHaveLength(5);
  });

  it("search query filters to messages containing the text (case-insensitive)", () => {
    const result = applyFilters(messages, { searchQuery: "tests" });
    const ids = result.map((m) => m.messageId);
    // "please run the tests" → matches
    expect(ids).toContain("user-1");
    // "Running tests now" → matches
    expect(ids).toContain("assistant-text-1");
    // "test output: PASSED" does NOT contain "tests"
    expect(ids).not.toContain("assistant-tool-1");
    // "Here are the results" / "file content" — no "tests"
    expect(ids).not.toContain("assistant-mixed-1");
  });

  it("search matches tool names", () => {
    const result = applyFilters(messages, { searchQuery: "bash" });
    expect(result.map((m) => m.messageId)).toContain("assistant-tool-1");
  });

  it("search matches tool output content", () => {
    const result = applyFilters(messages, { searchQuery: "PASSED" });
    const ids = result.map((m) => m.messageId);
    expect(ids).toContain("assistant-tool-1");
    expect(ids).not.toContain("user-1");
  });

  it("empty query returns all messages", () => {
    const result = applyFilters(messages, { searchQuery: "" });
    expect(result).toHaveLength(5);
  });

  it("type filter with only 'user' shows only user messages", () => {
    const result = applyFilters(messages, {
      typeFilter: new Set(["user"]),
    });
    expect(result.every((m) => m.role === "user")).toBe(true);
    expect(result).toHaveLength(2);
  });

  it("type filter with only 'assistant' shows only assistant text messages", () => {
    const result = applyFilters(messages, {
      typeFilter: new Set(["assistant"]),
    });
    // assistant-text-1 has text parts, assistant-mixed-1 has text parts
    // assistant-tool-1 has only tool parts — excluded
    const ids = result.map((m) => m.messageId);
    expect(ids).toContain("assistant-text-1");
    expect(ids).toContain("assistant-mixed-1"); // has text part
    expect(ids).not.toContain("assistant-tool-1"); // tool-only
    expect(ids).not.toContain("user-1");
  });

  it("type filter with only 'tool' shows only assistant messages with tool parts", () => {
    const result = applyFilters(messages, {
      typeFilter: new Set(["tool"]),
    });
    const ids = result.map((m) => m.messageId);
    expect(ids).toContain("assistant-tool-1");
    expect(ids).toContain("assistant-mixed-1"); // has tool part
    expect(ids).not.toContain("assistant-text-1"); // text-only
    expect(ids).not.toContain("user-1");
  });

  it("agent filter shows only messages from that agent", () => {
    const result = applyFilters(messages, { agentFilter: "worker" });
    expect(result.every((m) => m.agent === "worker")).toBe(true);
    expect(result).toHaveLength(3);
  });

  it("combined search + agent filter work together", () => {
    const result = applyFilters(messages, {
      searchQuery: "file",
      agentFilter: "worker",
    });
    const ids = result.map((m) => m.messageId);
    // "file content" is in assistant-mixed-1 (agent: worker)
    expect(ids).toContain("assistant-mixed-1");
    // user-2 has "linting" — no match for "file"
    expect(ids).not.toContain("user-2");
  });

  it("matchingPartIds contains correct part IDs for search matches", () => {
    const lowerQuery = "tests";
    const ids = new Set<string>();
    for (const message of messages) {
      for (const part of message.parts) {
        if (partMatchesQuery(part, lowerQuery)) {
          ids.add(part.partId);
        }
      }
    }
    // "please run the tests" → p1
    expect(ids.has("p1")).toBe(true);
    // "Running tests now" → p2
    expect(ids.has("p2")).toBe(true);
    // "test output: PASSED" does NOT contain "tests"
    expect(ids.has("p3")).toBe(false);
  });

  it("isFiltering is false when no filters active", () => {
    const defaultFilter = new Set<"user" | "assistant" | "tool">([
      "user",
      "assistant",
      "tool",
    ]);
    const isFiltering =
      "" !== "" ||
      null !== null ||
      defaultFilter.size !== 3 ||
      !["user", "assistant", "tool"].every((t) =>
        defaultFilter.has(t as "user" | "assistant" | "tool")
      );
    expect(isFiltering).toBe(false);
  });

  it("isFiltering is true when search query is set", () => {
    const defaultFilter = new Set<"user" | "assistant" | "tool">([
      "user",
      "assistant",
      "tool",
    ]);
    const searchQuery = "hello";
    const isFiltering =
      searchQuery.trim() !== "" ||
      null !== null ||
      defaultFilter.size !== 3 ||
      !["user", "assistant", "tool"].every((t) =>
        defaultFilter.has(t as "user" | "assistant" | "tool")
      );
    expect(isFiltering).toBe(true);
  });

  it("isFiltering is true when agent filter is set", () => {
    const defaultFilter = new Set<"user" | "assistant" | "tool">([
      "user",
      "assistant",
      "tool",
    ]);
    const agentFilter = "worker";
    const isFiltering =
      "".trim() !== "" ||
      agentFilter !== null ||
      defaultFilter.size !== 3 ||
      !["user", "assistant", "tool"].every((t) =>
        defaultFilter.has(t as "user" | "assistant" | "tool")
      );
    expect(isFiltering).toBe(true);
  });

  it("isFiltering is true when type filter is reduced", () => {
    const reducedFilter = new Set<"user" | "assistant" | "tool">(["user"]);
    const isFiltering =
      "".trim() !== "" ||
      null !== null ||
      reducedFilter.size !== 3 ||
      !["user", "assistant", "tool"].every((t) =>
        reducedFilter.has(t as "user" | "assistant" | "tool")
      );
    expect(isFiltering).toBe(true);
  });
});
