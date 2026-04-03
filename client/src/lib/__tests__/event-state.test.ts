import {
  ensureMessage,
  applyPartUpdate,
  applyTextDelta,
  isRelevantToSession,
} from "@/lib/event-state";
import type { AccumulatedMessage } from "@/lib/api-types";

// ─── Helpers ────────────────────────────────────────────────────────────────

function makeMessage(overrides: Partial<AccumulatedMessage> = {}): AccumulatedMessage {
  return {
    messageId: "msg-1",
    sessionId: "sess-1",
    role: "assistant",
    parts: [],
    ...overrides,
  };
}

// ─── ensureMessage ───────────────────────────────────────────────────────────

describe("ensureMessage", () => {
  it("appends a new message when id is not found", () => {
    const prev: AccumulatedMessage[] = [];
    const result = ensureMessage(prev, { id: "msg-1", sessionID: "sess-1", role: "user" });

    expect(result).toHaveLength(1);
    expect(result[0].messageId).toBe("msg-1");
    expect(result[0].sessionId).toBe("sess-1");
    expect(result[0].role).toBe("user");
    expect(result[0].parts).toEqual([]);
  });

  it("returns the same reference when the message already exists (idempotent)", () => {
    const prev = [makeMessage({ messageId: "msg-1" })];
    const result = ensureMessage(prev, { id: "msg-1" });

    expect(result).toBe(prev);
  });

  it("defaults role to assistant for any non-user role value", () => {
    const result = ensureMessage([], { id: "msg-2", role: "system" });
    expect(result[0].role).toBe("assistant");
  });

  it("defaults role to assistant when role is missing", () => {
    const result = ensureMessage([], { id: "msg-3" });
    expect(result[0].role).toBe("assistant");
  });

  it("defaults sessionId to empty string when sessionID is missing", () => {
    const result = ensureMessage([], { id: "msg-4" });
    expect(result[0].sessionId).toBe("");
  });

  it("sets createdAt from info.time.created", () => {
    const result = ensureMessage([], { id: "msg-5", time: { created: 1700000000 } });
    expect(result[0].createdAt).toBe(1700000000);
  });

  it("leaves createdAt undefined when info.time is absent", () => {
    const result = ensureMessage([], { id: "msg-6" });
    expect(result[0].createdAt).toBeUndefined();
  });

  it("returns a new array (immutability)", () => {
    const prev: AccumulatedMessage[] = [];
    const result = ensureMessage(prev, { id: "msg-7" });
    expect(result).not.toBe(prev);
  });

  it("preserves existing messages when appending", () => {
    const prev = [makeMessage({ messageId: "msg-1" })];
    const result = ensureMessage(prev, { id: "msg-2", sessionID: "sess-2" });

    expect(result).toHaveLength(2);
    expect(result[0].messageId).toBe("msg-1");
    expect(result[1].messageId).toBe("msg-2");
  });
});

// ─── applyPartUpdate ─────────────────────────────────────────────────────────

describe("applyPartUpdate", () => {
  it("auto-creates the message with role assistant if not present", () => {
    const result = applyPartUpdate([], {
      messageID: "msg-new",
      sessionID: "sess-1",
      type: "text",
      id: "part-1",
      text: "hello",
    });

    expect(result).toHaveLength(1);
    expect(result[0].messageId).toBe("msg-new");
    expect(result[0].role).toBe("assistant");
  });

  describe("text parts", () => {
    it("appends a new text part when the part id is not found", () => {
      const prev = [makeMessage()];
      const result = applyPartUpdate(prev, {
        messageID: "msg-1",
        sessionID: "sess-1",
        type: "text",
        id: "part-1",
        text: "hello",
      });

      expect(result[0].parts).toHaveLength(1);
      expect(result[0].parts[0]).toEqual({ partId: "part-1", type: "text", text: "hello" });
    });

    it("updates text on an existing text part", () => {
      const prev = [
        makeMessage({
          parts: [{ partId: "part-1", type: "text", text: "old" }],
        }),
      ];
      const result = applyPartUpdate(prev, {
        messageID: "msg-1",
        sessionID: "sess-1",
        type: "text",
        id: "part-1",
        text: "new",
      });

      expect(result[0].parts).toHaveLength(1);
      expect((result[0].parts[0] as { text: string }).text).toBe("new");
    });

    it("defaults text to empty string when part.text is missing", () => {
      const prev = [makeMessage()];
      const result = applyPartUpdate(prev, {
        messageID: "msg-1",
        sessionID: "sess-1",
        type: "text",
        id: "part-1",
      });

      expect((result[0].parts[0] as { text: string }).text).toBe("");
    });
  });

  describe("tool parts", () => {
    it("appends a new tool part", () => {
      const prev = [makeMessage()];
      const result = applyPartUpdate(prev, {
        messageID: "msg-1",
        sessionID: "sess-1",
        type: "tool",
        id: "tool-part-1",
        tool: "bash",
        callID: "call-abc",
        state: { status: "running" },
      });

      expect(result[0].parts).toHaveLength(1);
      expect(result[0].parts[0]).toEqual({
        partId: "tool-part-1",
        type: "tool",
        tool: "bash",
        callId: "call-abc",
        state: { status: "running" },
      });
    });

    it("replaces an existing tool part with updated state", () => {
      const prev = [
        makeMessage({
          parts: [
            {
              partId: "tool-part-1",
              type: "tool",
              tool: "bash",
              callId: "call-abc",
              state: { status: "running" },
            },
          ],
        }),
      ];
      const result = applyPartUpdate(prev, {
        messageID: "msg-1",
        sessionID: "sess-1",
        type: "tool",
        id: "tool-part-1",
        tool: "bash",
        callID: "call-abc",
        state: { status: "done" },
      });

      expect(result[0].parts).toHaveLength(1);
      expect((result[0].parts[0] as { state: unknown }).state).toEqual({ status: "done" });
    });

    it("defaults tool and callId to empty string when missing", () => {
      const prev = [makeMessage()];
      const result = applyPartUpdate(prev, {
        messageID: "msg-1",
        sessionID: "sess-1",
        type: "tool",
        id: "tool-part-2",
      });

      const part = result[0].parts[0] as { tool: string; callId: string };
      expect(part.tool).toBe("");
      expect(part.callId).toBe("");
    });
  });

  describe("step-finish parts", () => {
    it("accumulates cost and tokens onto the message", () => {
      const prev = [makeMessage()];
      const result = applyPartUpdate(prev, {
        messageID: "msg-1",
        sessionID: "sess-1",
        type: "step-finish",
        cost: 0.005,
        tokens: { input: 100, output: 50, reasoning: 10 },
      });

      expect(result[0].cost).toBeCloseTo(0.005);
      expect(result[0].tokens).toEqual({ input: 100, output: 50, reasoning: 10 });
    });

    it("adds cost and tokens onto existing values", () => {
      const prev = [
        makeMessage({
          cost: 0.01,
          tokens: { input: 200, output: 100, reasoning: 20 },
        }),
      ];
      const result = applyPartUpdate(prev, {
        messageID: "msg-1",
        sessionID: "sess-1",
        type: "step-finish",
        cost: 0.005,
        tokens: { input: 50, output: 25, reasoning: 5 },
      });

      expect(result[0].cost).toBeCloseTo(0.015);
      expect(result[0].tokens).toEqual({ input: 250, output: 125, reasoning: 25 });
    });

    it("handles missing cost and tokens gracefully (defaults to 0)", () => {
      const prev = [makeMessage()];
      const result = applyPartUpdate(prev, {
        messageID: "msg-1",
        sessionID: "sess-1",
        type: "step-finish",
      });

      expect(result[0].cost).toBe(0);
      expect(result[0].tokens).toEqual({ input: 0, output: 0, reasoning: 0 });
    });
  });

  it("returns message unchanged for unknown part type", () => {
    const prev = [makeMessage()];
    const result = applyPartUpdate(prev, {
      messageID: "msg-1",
      sessionID: "sess-1",
      type: "unknown-type",
    });

    expect(result[0]).toBe(prev[0]);
  });

  it("does not mutate messages for other sessions", () => {
    const other = makeMessage({ messageId: "msg-other", sessionId: "sess-other" });
    const target = makeMessage({ messageId: "msg-1" });
    const prev = [other, target];

    const result = applyPartUpdate(prev, {
      messageID: "msg-1",
      sessionID: "sess-1",
      type: "text",
      id: "part-1",
      text: "hi",
    });

    expect(result[0]).toBe(other);
    expect(result[1]).not.toBe(target);
  });
});

// ─── applyTextDelta ──────────────────────────────────────────────────────────

describe("applyTextDelta", () => {
  it("creates a new message and text part if message does not exist", () => {
    const result = applyTextDelta([], "msg-1", "part-1", "sess-1", "hello");

    expect(result).toHaveLength(1);
    expect(result[0].messageId).toBe("msg-1");
    expect(result[0].role).toBe("assistant");
    expect(result[0].parts[0]).toEqual({ partId: "part-1", type: "text", text: "hello" });
  });

  it("appends delta to an existing text part", () => {
    const prev = [
      makeMessage({
        parts: [{ partId: "part-1", type: "text", text: "hello" }],
      }),
    ];
    const result = applyTextDelta(prev, "msg-1", "part-1", "sess-1", " world");

    expect((result[0].parts[0] as { text: string }).text).toBe("hello world");
  });

  it("creates a new text part when partId is not found", () => {
    const prev = [makeMessage()];
    const result = applyTextDelta(prev, "msg-1", "part-new", "sess-1", "first");

    expect(result[0].parts).toHaveLength(1);
    expect((result[0].parts[0] as { text: string }).text).toBe("first");
  });

  it("returns a new array (immutability)", () => {
    const prev: AccumulatedMessage[] = [];
    const result = applyTextDelta(prev, "msg-1", "part-1", "sess-1", "x");
    expect(result).not.toBe(prev);
  });

  it("does not affect messages with a different messageId", () => {
    const other = makeMessage({ messageId: "msg-other" });
    const prev = [other];
    const result = applyTextDelta(prev, "msg-1", "part-1", "sess-1", "x");

    expect(result[0]).toBe(other);
    expect(result).toHaveLength(2);
  });

  it("accumulates multiple deltas correctly", () => {
    let state: AccumulatedMessage[] = [];
    state = applyTextDelta(state, "msg-1", "part-1", "sess-1", "foo");
    state = applyTextDelta(state, "msg-1", "part-1", "sess-1", " bar");
    state = applyTextDelta(state, "msg-1", "part-1", "sess-1", " baz");

    expect((state[0].parts[0] as { text: string }).text).toBe("foo bar baz");
  });
});

// ─── isRelevantToSession ─────────────────────────────────────────────────────

describe("isRelevantToSession", () => {
  const SESSION = "sess-abc";

  it("returns true for server.connected regardless of properties", () => {
    expect(isRelevantToSession("server.connected", {}, SESSION)).toBe(true);
  });

  it("returns true for server.heartbeat regardless of properties", () => {
    expect(isRelevantToSession("server.heartbeat", {}, SESSION)).toBe(true);
  });

  it("returns true for message.part.updated when part.sessionID matches", () => {
    expect(
      isRelevantToSession("message.part.updated", { part: { sessionID: SESSION } }, SESSION)
    ).toBe(true);
  });

  it("returns true for message.part.updated when top-level sessionID matches", () => {
    expect(
      isRelevantToSession("message.part.updated", { sessionID: SESSION }, SESSION)
    ).toBe(true);
  });

  it("returns false for message.part.updated when sessionID does not match", () => {
    expect(
      isRelevantToSession("message.part.updated", { part: { sessionID: "other" } }, SESSION)
    ).toBe(false);
  });

  it("returns true for message.updated when info.sessionID matches", () => {
    expect(
      isRelevantToSession("message.updated", { info: { sessionID: SESSION } }, SESSION)
    ).toBe(true);
  });

  it("returns false for message.updated when info.sessionID does not match", () => {
    expect(
      isRelevantToSession("message.updated", { info: { sessionID: "other" } }, SESSION)
    ).toBe(false);
  });

  it("returns true for session.updated when properties.sessionID matches", () => {
    expect(
      isRelevantToSession("session.updated", { sessionID: SESSION }, SESSION)
    ).toBe(true);
  });

  it("returns true for session.updated when properties.info.id matches", () => {
    expect(
      isRelevantToSession("session.updated", { info: { id: SESSION } }, SESSION)
    ).toBe(true);
  });

  it("returns false for session.updated when neither field matches", () => {
    expect(
      isRelevantToSession("session.updated", { sessionID: "other", info: { id: "other2" } }, SESSION)
    ).toBe(false);
  });

  it("returns true for permission.requested when sessionID matches", () => {
    expect(
      isRelevantToSession("permission.requested", { sessionID: SESSION }, SESSION)
    ).toBe(true);
  });

  it("returns false for permission.requested when sessionID does not match", () => {
    expect(
      isRelevantToSession("permission.requested", { sessionID: "other" }, SESSION)
    ).toBe(false);
  });

  it("returns true for message.part.delta when sessionID matches", () => {
    expect(
      isRelevantToSession("message.part.delta", { sessionID: SESSION }, SESSION)
    ).toBe(true);
  });

  it("returns false for message.part.delta when sessionID does not match", () => {
    expect(
      isRelevantToSession("message.part.delta", { sessionID: "other" }, SESSION)
    ).toBe(false);
  });

  it("returns false for an unknown event type", () => {
    expect(
      isRelevantToSession("some.unknown.event", { sessionID: SESSION }, SESSION)
    ).toBe(false);
  });
});
