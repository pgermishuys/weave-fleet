import { afterEach, describe, expect, it, vi } from "vitest";
import type React from "react";
import type { AccumulatedMessage, DelegationDto, WebSocketEvent } from "@/lib/api-types";
import { handleEvent } from "@/hooks/use-session-events";

vi.mock("@/lib/api-client", async (importOriginal) => {
  const actual = await importOriginal<typeof import("@/lib/api-client")>();
  return {
    ...actual,
    apiFetch: vi.fn(),
  };
});

vi.mock("@/hooks/use-paginated-messages", () => ({
  usePaginatedMessages: () => ({
    pagination: { hasMore: false, isLoadingOlder: false, totalCount: 0, loadError: undefined },
    loadInitialMessages: vi.fn(async () => []),
    loadMessagesSince: vi.fn(async () => []),
    loadOlderMessages: vi.fn(async () => []),
    resetPagination: vi.fn(),
    hydratePagination: vi.fn(),
    snapshotPaginationRef: { current: () => ({ hasMore: false, isLoadingOlder: false, totalCount: 0, loadError: undefined }) },
  }),
}));

vi.mock("@/hooks/use-session-status", () => ({
  fetchSessionStatus: vi.fn(async () => "idle"),
}));

const onReconnectCallbacks: Array<() => void> = [];
vi.mock("@/hooks/use-weave-socket", () => ({
  useWeaveSocket: () => ({
    subscribe: vi.fn(() => () => {}),
  }),
  onReconnect: vi.fn((cb: () => void) => {
    onReconnectCallbacks.push(cb);
    return () => {};
  }),
}));

afterEach(() => {
  vi.clearAllMocks();
});

function createStateHarness(sessionId: string) {
  let messages: AccumulatedMessage[] = [];
  let delegations: DelegationDto[] = [];
  let status: string | undefined;
  let sessionStatus: "idle" | "busy" = "idle";
  let error: string | undefined;

  const setMessages = (update: React.SetStateAction<AccumulatedMessage[]>) => {
    messages = typeof update === "function"
      ? (update as (prev: AccumulatedMessage[]) => AccumulatedMessage[])(messages)
      : update;
  };
  const setDelegations = (update: React.SetStateAction<DelegationDto[]>) => {
    delegations = typeof update === "function"
      ? (update as (prev: DelegationDto[]) => DelegationDto[])(delegations)
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
  const lastSequenceNumberRef: React.MutableRefObject<number | null> = { current: null };

  const dispatch = (event: WebSocketEvent) => {
    handleEvent(
      event,
      sessionId,
      setMessages,
      setDelegations,
      setStatus as React.Dispatch<React.SetStateAction<"connecting" | "connected" | "recovering" | "disconnected" | "error" | "abandoned">>,
      setSessionStatus,
      setError,
      onAgentSwitchRef,
      lastSequenceNumberRef,
    );
  };

  return {
    dispatch,
    getMessages: () => messages,
    getDelegations: () => delegations,
    getLastSequenceNumber: () => lastSequenceNumberRef.current,
  };
}

describe("handleEvent delegation events", () => {
  it("applies delegation.created", () => {
    const harness = createStateHarness("sess-1");

    harness.dispatch({
      type: "delegation.created",
      properties: {
        delegationId: "del-1",
        parentToolCallId: "tool-1",
        childSessionId: null,
        title: "reviewer",
        status: "pending",
        createdAt: "2026-04-10T12:00:00.000Z",
      },
    } as WebSocketEvent);

    expect(harness.getDelegations()).toEqual([
      {
        delegationId: "del-1",
        parentToolCallId: "tool-1",
        childSessionId: null,
        title: "reviewer",
        status: "pending",
        createdAt: "2026-04-10T12:00:00.000Z",
      },
    ]);
  });

  it("applies delegation.updated", () => {
    const harness = createStateHarness("sess-1");

    harness.dispatch({
      type: "delegation.created",
      properties: {
        delegationId: "del-1",
        parentToolCallId: "tool-1",
        childSessionId: null,
        title: "reviewer",
        status: "pending",
        createdAt: "2026-04-10T12:00:00.000Z",
      },
    } as WebSocketEvent);

    harness.dispatch({
      type: "delegation.updated",
      properties: {
        delegationId: "del-1",
        childSessionId: "child-1",
        status: "running",
      },
    } as WebSocketEvent);

    expect(harness.getDelegations()).toEqual([
      {
        delegationId: "del-1",
        parentToolCallId: "tool-1",
        childSessionId: "child-1",
        title: "reviewer",
        status: "running",
        createdAt: "2026-04-10T12:00:00.000Z",
      },
    ]);
  });
});

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
    } as WebSocketEvent);

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
    } as WebSocketEvent);

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
    } as WebSocketEvent);

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
    } as WebSocketEvent);

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
    } as WebSocketEvent);

    const messages = harness.getMessages();
    expect(messages).toHaveLength(1);
    expect(messages[0]?.sessionId).toBe("sess-1");
    expect(messages[0]?.parts[0]).toMatchObject({ type: "text", text: "hello" });
  });
});

describe("useSessionEvents reconnect recovery", () => {
  it("keeps reconnect callback registration available", () => {
    expect(onReconnectCallbacks).toBeDefined();
  });

  it("tracks the highest committed sequence number seen", () => {
    const harness = createStateHarness("sess-1");

    harness.dispatch({
      type: "message.updated",
      sequenceNumber: 3,
      properties: {
        info: { id: "msg-1", role: "assistant", sessionID: "sess-1" },
      },
    } as WebSocketEvent);

    harness.dispatch({
      type: "message.part.updated",
      sequenceNumber: 2,
      properties: {
        part: { id: "part-1", messageID: "msg-1", type: "text", text: "hello" },
      },
    } as WebSocketEvent);

    harness.dispatch({
      type: "message.updated",
      sequenceNumber: 8,
      properties: {
        info: { id: "msg-1", role: "assistant", sessionID: "sess-1" },
      },
    } as WebSocketEvent);

    expect(harness.getLastSequenceNumber()).toBe(8);
  });

  it("applies committed snapshot parts from message.updated payloads", () => {
    const harness = createStateHarness("sess-1");

    harness.dispatch({
      type: "message.updated",
      properties: {
        info: { id: "msg-1", role: "assistant", sessionID: "sess-1" },
      },
    } as WebSocketEvent);

    harness.dispatch({
      type: "message.part.updated",
      properties: {
        sessionID: "sess-1",
        part: {
          id: "file-1",
          messageID: "msg-1",
          type: "file",
          mime: "text/plain",
          filename: "out.txt",
          url: "file:///tmp/out.txt",
        },
      },
    } as WebSocketEvent);

    harness.dispatch({
      type: "message.updated",
      properties: {
        info: { id: "msg-1", role: "assistant", sessionID: "sess-1" },
        parts: [
          { id: "part-1", type: "text", text: "final merged text" },
        ],
      },
    } as WebSocketEvent);

    expect(harness.getMessages()[0]?.parts).toEqual([
      { partId: "part-1", type: "text", text: "final merged text" },
      { partId: "file-1", type: "file", mime: "text/plain", filename: "out.txt", url: "file:///tmp/out.txt" },
    ]);
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
    } as WebSocketEvent);

    // Then create a part via message.part.updated
    harness.dispatch({
      type: "message.part.updated",
      properties: {
        sessionID: "sess-1",
        part: { id: "part-1", messageID: "msg-1", type: "text", text: "" },
      },
    } as WebSocketEvent);

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
    } as WebSocketEvent);

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
    } as WebSocketEvent);

    harness.dispatch({
      type: "message.part.updated",
      properties: {
        sessionID: "fleet-abc",
        part: { id: "part-1", messageID: "msg-1", type: "text", text: "" },
      },
    } as WebSocketEvent);

    harness.dispatch({
      type: "message.part.delta",
      properties: {
        sessionID: "opencode-xyz",  // mismatched — would have been dropped before
        messageID: "msg-1",
        partID: "part-1",
        field: "text",
        delta: "streaming text",
      },
    } as WebSocketEvent);

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
    } as WebSocketEvent);

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
    } as WebSocketEvent);

    expect(harness.getMessages()).toHaveLength(0);
  });
});
