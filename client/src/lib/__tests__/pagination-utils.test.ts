/**
 * Unit tests for `pagination-utils` — pure-function verification.
 *
 * Tests sliceMessages, prependMessages, and convertSDKMessageToAccumulated.
 */

import { describe, it, expect } from "vitest";
import {
  sliceMessages,
  prependMessages,
  convertSDKMessageToAccumulated,
} from "@/lib/pagination-utils";
import type { AccumulatedMessage } from "@/lib/api-types";
import type { SDKMessage } from "@/lib/pagination-utils";

// ─── Helpers ────────────────────────────────────────────────────────────────

/** Create a minimal SDK-shaped message for sliceMessages tests. */
function makeSDKMsg(id: string, createdAt = 0): { info: { id: string }; parts: never[] } {
  return { info: { id }, parts: [] as never[] };
}

/** Create a minimal AccumulatedMessage for prependMessages tests. */
function makeAccMsg(messageId: string): AccumulatedMessage {
  return {
    messageId,
    sessionId: "sess-1",
    role: "assistant",
    parts: [],
  };
}

/** Create a full SDKMessage for convertSDKMessageToAccumulated tests. */
function makeFullSDKMsg(overrides: Partial<SDKMessage> = {}): SDKMessage {
  return {
    info: {
      id: "msg-1",
      sessionID: "sess-1",
      role: "assistant",
      time: { created: 1000, completed: 2000 },
      agent: "build",
      modelID: "gpt-4",
      parentID: "msg-0",
      ...(overrides.info ?? {}),
    },
    parts: overrides.parts ?? [],
  };
}

// ─── sliceMessages ──────────────────────────────────────────────────────────

describe("sliceMessages", () => {
  const msgs = Array.from({ length: 10 }, (_, i) => makeSDKMsg(`msg-${i}`));

  it("returns last N messages when no cursor", () => {
    const result = sliceMessages(msgs, { limit: 3 });
    expect(result.messages).toHaveLength(3);
    expect(result.messages.map((m) => m.info.id)).toEqual([
      "msg-7",
      "msg-8",
      "msg-9",
    ]);
  });

  it("returns correct slice when before cursor is provided", () => {
    const result = sliceMessages(msgs, { limit: 3, before: "msg-5" });
    expect(result.messages).toHaveLength(3);
    expect(result.messages.map((m) => m.info.id)).toEqual([
      "msg-2",
      "msg-3",
      "msg-4",
    ]);
  });

  it("returns hasMore: false when returning from the beginning", () => {
    const result = sliceMessages(msgs, { limit: 3, before: "msg-2" });
    expect(result.messages.map((m) => m.info.id)).toEqual(["msg-0", "msg-1"]);
    expect(result.pagination.hasMore).toBe(false);
  });

  it("returns hasMore: true when there are older messages", () => {
    const result = sliceMessages(msgs, { limit: 3 });
    expect(result.pagination.hasMore).toBe(true);
  });

  it("handles empty array", () => {
    const result = sliceMessages([], { limit: 5 });
    expect(result.messages).toEqual([]);
    expect(result.pagination.hasMore).toBe(false);
    expect(result.pagination.oldestMessageId).toBeNull();
    expect(result.pagination.totalCount).toBe(0);
  });

  it("handles cursor not found (falls back to tail)", () => {
    const result = sliceMessages(msgs, { limit: 3, before: "nonexistent" });
    expect(result.messages).toHaveLength(3);
    expect(result.messages.map((m) => m.info.id)).toEqual([
      "msg-7",
      "msg-8",
      "msg-9",
    ]);
  });

  it("handles limit > total count", () => {
    const result = sliceMessages(msgs, { limit: 100 });
    expect(result.messages).toHaveLength(10);
    expect(result.pagination.hasMore).toBe(false);
  });

  it("returns correct totalCount", () => {
    const result = sliceMessages(msgs, { limit: 3 });
    expect(result.pagination.totalCount).toBe(10);
  });

  it("returns correct oldestMessageId", () => {
    const result = sliceMessages(msgs, { limit: 3 });
    expect(result.pagination.oldestMessageId).toBe("msg-7");
  });

  it("returns correct oldestMessageId with cursor", () => {
    const result = sliceMessages(msgs, { limit: 3, before: "msg-5" });
    expect(result.pagination.oldestMessageId).toBe("msg-2");
  });

  it("returns all messages when limit equals total", () => {
    const result = sliceMessages(msgs, { limit: 10 });
    expect(result.messages).toHaveLength(10);
    expect(result.pagination.hasMore).toBe(false);
    expect(result.pagination.oldestMessageId).toBe("msg-0");
  });

  it("returns hasMore: false when cursor is at the start", () => {
    const result = sliceMessages(msgs, { limit: 5, before: "msg-0" });
    expect(result.messages).toEqual([]);
    expect(result.pagination.hasMore).toBe(false);
  });
});

// ─── prependMessages ────────────────────────────────────────────────────────

describe("prependMessages", () => {
  it("prepends older messages before existing", () => {
    const existing = [makeAccMsg("msg-5"), makeAccMsg("msg-6")];
    const older = [makeAccMsg("msg-3"), makeAccMsg("msg-4")];
    const result = prependMessages(existing, older);
    expect(result.map((m) => m.messageId)).toEqual([
      "msg-3",
      "msg-4",
      "msg-5",
      "msg-6",
    ]);
  });

  it("deduplicates by messageId (SSE may have already added the message)", () => {
    const existing = [makeAccMsg("msg-4"), makeAccMsg("msg-5")];
    const older = [makeAccMsg("msg-3"), makeAccMsg("msg-4")]; // msg-4 is a duplicate
    const result = prependMessages(existing, older);
    expect(result.map((m) => m.messageId)).toEqual([
      "msg-3",
      "msg-4",
      "msg-5",
    ]);
  });

  it("returns existing array unchanged if older is empty", () => {
    const existing = [makeAccMsg("msg-1")];
    const result = prependMessages(existing, []);
    expect(result).toBe(existing); // same reference
  });

  it("preserves order (older first, then existing)", () => {
    const existing = [makeAccMsg("b"), makeAccMsg("c")];
    const older = [makeAccMsg("a")];
    const result = prependMessages(existing, older);
    expect(result.map((m) => m.messageId)).toEqual(["a", "b", "c"]);
  });

  it("returns existing array unchanged when all older messages are duplicates", () => {
    const existing = [makeAccMsg("msg-1"), makeAccMsg("msg-2")];
    const older = [makeAccMsg("msg-1"), makeAccMsg("msg-2")];
    const result = prependMessages(existing, older);
    expect(result).toBe(existing);
  });
});

// ─── convertSDKMessageToAccumulated ─────────────────────────────────────────

describe("convertSDKMessageToAccumulated", () => {
  it("converts text parts correctly", () => {
    const msg = makeFullSDKMsg({
      parts: [
        {
          id: "p1",
          messageID: "msg-1",
          sessionID: "sess-1",
          type: "text",
          text: "Hello world",
        },
      ],
    });
    const result = convertSDKMessageToAccumulated(msg);
    expect(result.parts).toHaveLength(1);
    expect(result.parts[0]).toEqual({
      partId: "p1",
      type: "text",
      text: "Hello world",
    });
  });

  it("converts tool parts correctly", () => {
    const msg = makeFullSDKMsg({
      parts: [
        {
          id: "p2",
          messageID: "msg-1",
          sessionID: "sess-1",
          type: "tool",
          tool: "read_file",
          callID: "call-1",
          state: { status: "completed" },
        },
      ],
    });
    const result = convertSDKMessageToAccumulated(msg);
    expect(result.parts).toHaveLength(1);
    expect(result.parts[0]).toEqual({
      partId: "p2",
      type: "tool",
      tool: "read_file",
      callId: "call-1",
      state: { status: "completed" },
    });
  });

  it("accumulates step-finish cost/tokens", () => {
    const msg = makeFullSDKMsg({
      parts: [
        {
          id: "sf1",
          messageID: "msg-1",
          sessionID: "sess-1",
          type: "step-finish",
          cost: 0.01,
          tokens: { input: 100, output: 50, reasoning: 10 },
        },
        {
          id: "sf2",
          messageID: "msg-1",
          sessionID: "sess-1",
          type: "step-finish",
          cost: 0.02,
          tokens: { input: 200, output: 100, reasoning: 20 },
        },
      ],
    });
    const result = convertSDKMessageToAccumulated(msg);
    expect(result.parts).toHaveLength(0); // step-finish parts are not added to parts[]
    expect(result.cost).toBe(0.03);
    expect(result.tokens).toEqual({ input: 300, output: 150, reasoning: 30 });
  });

  it("falls back to info-level cost when no step-finish parts", () => {
    const msg = makeFullSDKMsg({
      info: {
        id: "msg-1",
        sessionID: "sess-1",
        role: "assistant",
        cost: 0.05,
        tokens: { input: 500, output: 250, reasoning: 50 },
      },
      parts: [],
    });
    const result = convertSDKMessageToAccumulated(msg);
    expect(result.cost).toBe(0.05);
    expect(result.tokens).toEqual({ input: 500, output: 250, reasoning: 50 });
  });

  it("handles missing optional fields", () => {
    const msg: SDKMessage = {
      info: {
        id: "msg-2",
        sessionID: "sess-1",
        role: "user",
      },
      parts: [],
    };
    const result = convertSDKMessageToAccumulated(msg);
    expect(result.messageId).toBe("msg-2");
    expect(result.role).toBe("user");
    expect(result.createdAt).toBeUndefined();
    expect(result.completedAt).toBeUndefined();
    expect(result.agent).toBeUndefined();
    expect(result.modelID).toBeUndefined();
    expect(result.parentID).toBeUndefined();
    expect(result.cost).toBe(0);
    expect(result.tokens).toBeUndefined();
  });

  it("maps user role correctly", () => {
    const msg = makeFullSDKMsg({
      info: { id: "m1", sessionID: "s1", role: "user" },
    });
    expect(convertSDKMessageToAccumulated(msg).role).toBe("user");
  });

  it("maps non-user role to assistant", () => {
    const msg = makeFullSDKMsg({
      info: { id: "m1", sessionID: "s1", role: "system" },
    });
    expect(convertSDKMessageToAccumulated(msg).role).toBe("assistant");
  });

  it("handles text part with missing text field", () => {
    const msg = makeFullSDKMsg({
      parts: [
        {
          id: "p1",
          messageID: "msg-1",
          sessionID: "sess-1",
          type: "text",
          // text is intentionally omitted
        },
      ],
    });
    const result = convertSDKMessageToAccumulated(msg);
    expect(result.parts[0]).toEqual({
      partId: "p1",
      type: "text",
      text: "",
    });
  });
});
