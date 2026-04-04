import { describe, expect, it, vi } from "vitest";
import type React from "react";
import type { AccumulatedMessage, SSEEvent } from "@/lib/api-types";
import { handleEvent } from "@/hooks/use-session-events";

function createStateHarness(sessionId: string) {
  let messages: AccumulatedMessage[] = [];
  let status: string | undefined;
  let sessionStatus: "idle" | "busy" = "idle";
  let error: string | undefined;

  const setMessages = (update: React.SetStateAction<AccumulatedMessage[]>) => {
    messages = typeof update === "function"
      ? (update as (prev: AccumulatedMessage[]) => AccumulatedMessage[])(messages)
      : update;
  };
  const setStatus = (update: React.SetStateAction<string>) => {
    status = typeof update === "function"
      ? (update as (prev: string | undefined) => string)(status)
      : update;
  };
  const setSessionStatus = (update: React.SetStateAction<"idle" | "busy">) => {
    sessionStatus = typeof update === "function"
      ? (update as (prev: "idle" | "busy") => "idle" | "busy")(sessionStatus)
      : update;
  };
  const setError = (update: React.SetStateAction<string | undefined>) => {
    error = typeof update === "function"
      ? (update as (prev: string | undefined) => string | undefined)(error)
      : update;
  };

  const onAgentSwitchRef: React.MutableRefObject<((agent: string) => void) | undefined> = {
    current: vi.fn(),
  };
  const lastMessageIdRef: React.MutableRefObject<string | null> = { current: null };

  const dispatch = (event: SSEEvent) => {
    handleEvent(
      event,
      sessionId,
      setMessages,
      setStatus as React.Dispatch<React.SetStateAction<"connecting" | "connected" | "recovering" | "disconnected" | "error" | "abandoned">>,
      setSessionStatus,
      setError,
      onAgentSwitchRef,
      lastMessageIdRef,
    );
  };

  return {
    dispatch,
    getMessages: () => messages,
  };
}

describe("handleEvent message.part.updated", () => {
  it("applies text part when only top-level sessionID is present", () => {
    const harness = createStateHarness("sess-1");

    harness.dispatch({
      type: "message.updated",
      properties: {
        info: {
          id: "msg-1",
          role: "assistant",
          sessionID: "sess-1",
        },
      },
    } as SSEEvent);

    harness.dispatch({
      type: "message.part.updated",
      properties: {
        sessionID: "sess-1",
        part: {
          id: "part-1",
          messageID: "msg-1",
          type: "text",
          text: "hello",
        },
      },
    } as SSEEvent);

    const messages = harness.getMessages();
    expect(messages).toHaveLength(1);
    expect(messages[0]?.parts).toHaveLength(1);
    expect(messages[0]?.parts[0]).toMatchObject({ type: "text", text: "hello" });
  });

  it("ignores part updates missing messageID", () => {
    const harness = createStateHarness("sess-1");

    harness.dispatch({
      type: "message.part.updated",
      properties: {
        sessionID: "sess-1",
        part: {
          id: "part-1",
          type: "text",
          text: "hello",
          // messageID intentionally omitted
        },
      },
    } as SSEEvent);

    expect(harness.getMessages()).toHaveLength(0);
  });

  it("applies text part when event sessionID differs from fleet sessionID (backend rewrites to fleet ID)", () => {
    // After TODO 4, the backend rewrites event payloads to contain fleet IDs.
    // The session ID check was removed so topic routing scopes events instead.
    // Even if the event arrives with a different sessionID, it is applied —
    // the sessionID in the applied part is overridden with the fleet sessionId.
    const harness = createStateHarness("fleet-abc");

    harness.dispatch({
      type: "message.part.updated",
      properties: {
        sessionID: "opencode-xyz",  // mismatched — would have been dropped before
        part: {
          id: "part-1",
          messageID: "msg-1",
          type: "text",
          text: "hello",
        },
      },
    } as SSEEvent);

    const messages = harness.getMessages();
    // Part is applied regardless of sessionID mismatch
    expect(messages).toHaveLength(1);
    // sessionID is overridden with the fleet sessionId
    expect(messages[0]?.sessionId).toBe("fleet-abc");
    expect(messages[0]?.parts[0]).toMatchObject({ type: "text", text: "hello" });
  });

  it("creates new message with concrete sessionID on fallback", () => {
    const harness = createStateHarness("sess-1");

    harness.dispatch({
      type: "message.part.updated",
      properties: {
        sessionID: "sess-1",
        part: {
          id: "part-1",
          messageID: "msg-1",
          type: "text",
          text: "hello",
        },
      },
    } as SSEEvent);

    const messages = harness.getMessages();
    expect(messages).toHaveLength(1);
    expect(messages[0]?.sessionId).toBe("sess-1");
    expect(messages[0]?.parts[0]).toMatchObject({ type: "text", text: "hello" });
  });
});

describe("handleEvent message.part.delta", () => {
  it("applies text delta correctly", () => {
    const harness = createStateHarness("sess-1");

    // First create a message via message.updated
    harness.dispatch({
      type: "message.updated",
      properties: {
        info: { id: "msg-1", role: "assistant", sessionID: "sess-1" },
      },
    } as SSEEvent);

    // Then create a part via message.part.updated
    harness.dispatch({
      type: "message.part.updated",
      properties: {
        sessionID: "sess-1",
        part: { id: "part-1", messageID: "msg-1", type: "text", text: "" },
      },
    } as SSEEvent);

    // Apply delta
    harness.dispatch({
      type: "message.part.delta",
      properties: {
        sessionID: "sess-1",
        messageID: "msg-1",
        partID: "part-1",
        field: "text",
        delta: "Hello world",
      },
    } as SSEEvent);

    const messages = harness.getMessages();
    expect(messages[0]?.parts[0]).toMatchObject({ type: "text", text: "Hello world" });
  });

  it("applies text delta when event sessionID differs from fleet sessionID", () => {
    // Session ID check was removed — delta should be applied regardless of sessionID in payload.
    const harness = createStateHarness("fleet-abc");

    harness.dispatch({
      type: "message.updated",
      properties: {
        info: { id: "msg-1", role: "assistant", sessionID: "fleet-abc" },
      },
    } as SSEEvent);

    harness.dispatch({
      type: "message.part.updated",
      properties: {
        sessionID: "fleet-abc",
        part: { id: "part-1", messageID: "msg-1", type: "text", text: "" },
      },
    } as SSEEvent);

    harness.dispatch({
      type: "message.part.delta",
      properties: {
        sessionID: "opencode-xyz",  // mismatched — would have been dropped before
        messageID: "msg-1",
        partID: "part-1",
        field: "text",
        delta: "streaming text",
      },
    } as SSEEvent);

    const messages = harness.getMessages();
    expect(messages[0]?.parts[0]).toMatchObject({ type: "text", text: "streaming text" });
  });

  it("ignores delta for non-text field", () => {
    const harness = createStateHarness("sess-1");

    harness.dispatch({
      type: "message.part.delta",
      properties: {
        sessionID: "sess-1",
        messageID: "msg-1",
        partID: "part-1",
        field: "image",  // not "text"
        delta: "some-data",
      },
    } as SSEEvent);

    expect(harness.getMessages()).toHaveLength(0);
  });

  it("ignores delta when messageID is missing", () => {
    const harness = createStateHarness("sess-1");

    harness.dispatch({
      type: "message.part.delta",
      properties: {
        sessionID: "sess-1",
        // messageID intentionally omitted
        partID: "part-1",
        field: "text",
        delta: "hello",
      },
    } as SSEEvent);

    expect(harness.getMessages()).toHaveLength(0);
  });
});
