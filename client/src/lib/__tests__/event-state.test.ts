import { describe, expect, it } from "vitest";
import { applyPartUpdate } from "@/lib/event-state";

describe("applyPartUpdate", () => {
  it("creates_a_message_for_the_first_tool_part", () => {
    const messages = applyPartUpdate([], {
      messageID: "message-1",
      sessionID: "session-1",
      id: "tool-1",
      type: "tool",
      tool: "bash",
      callID: "call-1",
      state: { status: "running" },
    });

    expect(messages).toEqual([
      {
        messageId: "message-1",
        sessionId: "session-1",
        role: "assistant",
        parts: [
          {
            partId: "tool-1",
            type: "tool",
            tool: "bash",
            callId: "call-1",
            state: { status: "running" },
          },
        ],
      },
    ]);
  });

  it("merges_sequential_tool_state_updates", () => {
    const initialMessages = applyPartUpdate([], {
      messageID: "message-1",
      sessionID: "session-1",
      id: "tool-1",
      type: "tool",
      tool: "bash",
      callID: "call-1",
      state: { status: "running", stdout: "first chunk" },
    });

    const updatedMessages = applyPartUpdate(initialMessages, {
      messageID: "message-1",
      sessionID: "session-1",
      id: "tool-1",
      type: "tool",
      tool: "bash",
      callID: "call-1",
      state: { stderr: "warning", exitCode: 0 },
    });

    expect(updatedMessages).toHaveLength(1);
    expect(updatedMessages[0]?.parts).toEqual([
      {
        partId: "tool-1",
        type: "tool",
        tool: "bash",
        callId: "call-1",
        state: {
          status: "running",
          stdout: "first chunk",
          stderr: "warning",
          exitCode: 0,
        },
      },
    ]);
  });

  it("overwrites_existing_tool_state_keys_with_latest_values", () => {
    const initialMessages = applyPartUpdate([], {
      messageID: "message-1",
      sessionID: "session-1",
      id: "tool-1",
      type: "tool",
      tool: "bash",
      callID: "call-1",
      state: { status: "running", stdout: "first chunk", exitCode: 1 },
    });

    const updatedMessages = applyPartUpdate(initialMessages, {
      messageID: "message-1",
      sessionID: "session-1",
      id: "tool-1",
      type: "tool",
      tool: "bash",
      callID: "call-1",
      state: { status: "completed", exitCode: 0 },
    });

    expect(updatedMessages[0]?.parts).toEqual([
      {
        partId: "tool-1",
        type: "tool",
        tool: "bash",
        callId: "call-1",
        state: {
          status: "completed",
          stdout: "first chunk",
          exitCode: 0,
        },
      },
    ]);
  });
});
