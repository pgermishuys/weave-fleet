import { describe, expect, it } from "vitest";
import { applyPartUpdate, mergeMessageUpdate } from "@/lib/event-state";

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

describe("mergeMessageUpdate preserves file parts from snapshot", () => {
  it("includes file parts from committed snapshot even when no prior file part existed", () => {
    // Simulate: message exists with a text part only (file part.updated was missed)
    const messages = [
      {
        messageId: "msg-1",
        sessionId: "session-1",
        role: "user" as const,
        parts: [
          { partId: "text-1", type: "text" as const, text: "hello" },
        ],
      },
    ];

    const updated = mergeMessageUpdate(messages, {
      id: "msg-1",
      time: { completed: 1234 },
      parts: [
        { id: "text-1", type: "text", text: "hello" },
        { id: "file-1", type: "file", mime: "image/png", filename: "screenshot.png", url: "data:image/png;base64,abc" },
      ],
    });

    const msg = updated.find((m) => m.messageId === "msg-1");
    expect(msg).toBeDefined();
    const filePart = msg!.parts.find((p) => p.type === "file");
    expect(filePart).toBeDefined();
    expect(filePart).toMatchObject({
      partId: "file-1",
      type: "file",
      mime: "image/png",
      filename: "screenshot.png",
      url: "data:image/png;base64,abc",
    });
  });

  it("preserves file parts already accumulated when snapshot arrives without them", () => {
    // Simulate: file part arrived via part.updated, then message.updated arrives without file in parts array
    const messages = [
      {
        messageId: "msg-1",
        sessionId: "session-1",
        role: "user" as const,
        parts: [
          { partId: "text-1", type: "text" as const, text: "hello" },
          { partId: "file-1", type: "file" as const, mime: "image/png", filename: "screenshot.png", url: "data:image/png;base64,abc" },
        ],
      },
    ];

    const updated = mergeMessageUpdate(messages, {
      id: "msg-1",
      time: { completed: 1234 },
      parts: [
        { id: "text-1", type: "text", text: "hello" },
      ],
    });

    const msg = updated.find((m) => m.messageId === "msg-1");
    expect(msg).toBeDefined();
    const filePart = msg!.parts.find((p) => p.type === "file");
    expect(filePart).toBeDefined();
    expect(filePart).toMatchObject({
      partId: "file-1",
      type: "file",
      mime: "image/png",
    });
  });
});
