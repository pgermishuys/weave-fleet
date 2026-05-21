import { describe, expect, it } from "vitest";
import { applyPartUpdate, applyTextDelta, mergeMessageUpdate } from "@/lib/event-state";

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
  it("keeps_user_prompt_before_assistant_response_when_assistant_timestamp_arrives_late", () => {
    const messages = [
      {
        messageId: "user-message-1",
        sessionId: "session-1",
        role: "user" as const,
        createdAt: 1000,
        parts: [
          { partId: "user-text-1", type: "text" as const, text: "Explain stream ordering." },
        ],
      },
      {
        messageId: "assistant-message-1",
        sessionId: "session-1",
        role: "assistant" as const,
        parts: [
          { partId: "assistant-text-1", type: "text" as const, text: "Partial response" },
        ],
      },
    ];

    const updated = mergeMessageUpdate(messages, {
      id: "assistant-message-1",
      time: { created: 1000, completed: 2000 },
      parts: [
        { id: "assistant-text-1", type: "text", text: "Complete response" },
      ],
    });

    expect(updated.filter((message) => message.role === "user")).toHaveLength(1);
    expect(updated.filter((message) => message.role === "assistant")).toHaveLength(1);
    expect(updated.map((message) => message.role)).toEqual(["user", "assistant"]);

    const assistant = updated.find((message) => message.role === "assistant");
    expect(assistant?.parts).toEqual([
      {
        partId: "assistant-text-1",
        type: "text",
        text: "Complete response",
      },
    ]);
  });

  it("does_not_duplicate_assistant_message_and_preserves_longer_part_when_lifecycle_arrives_after_part_update", () => {
    const messages = applyPartUpdate([
      {
        messageId: "user-message-1",
        sessionId: "session-1",
        role: "user" as const,
        createdAt: 1000,
        parts: [
          { partId: "user-text-1", type: "text" as const, text: "Explain out-of-order message events." },
        ],
      },
    ], {
      messageID: "assistant-message-1",
      sessionID: "session-1",
      id: "assistant-text-1",
      type: "text",
      text: "Part update text that is longer than the lifecycle snapshot.",
    });

    const updated = mergeMessageUpdate(messages, {
      id: "assistant-message-1",
      role: "assistant",
      sessionID: "session-1",
      time: { created: 1000, completed: 2000 },
      parts: [
        { id: "assistant-text-1", type: "text", text: "Lifecycle text." },
      ],
    });

    expect(updated.filter((message) => message.messageId === "assistant-message-1")).toHaveLength(1);
    expect(updated.filter((message) => message.role === "user")).toHaveLength(1);
    expect(updated.filter((message) => message.role === "assistant")).toHaveLength(1);
    expect(updated.map((message) => message.role)).toEqual(["user", "assistant"]);
    expect(updated.map((message) => message.messageId)).toEqual(["user-message-1", "assistant-message-1"]);

    const assistant = updated.find((message) => message.messageId === "assistant-message-1");
    expect(assistant).toMatchObject({
      role: "assistant",
      createdAt: 1000,
      completedAt: 2000,
    });
    expect(assistant?.parts).toEqual([
      {
        partId: "assistant-text-1",
        type: "text",
        text: "Part update text that is longer than the lifecycle snapshot.",
      },
    ]);
  });

  it("does_not_duplicate_assistant_message_and_replaces_shorter_part_when_lifecycle_arrives_after_part_update", () => {
    const messages = applyPartUpdate([
      {
        messageId: "user-message-1",
        sessionId: "session-1",
        role: "user" as const,
        createdAt: 1000,
        parts: [
          { partId: "user-text-1", type: "text" as const, text: "Explain out-of-order message events." },
        ],
      },
    ], {
      messageID: "assistant-message-1",
      sessionID: "session-1",
      id: "assistant-text-1",
      type: "text",
      text: "Partial.",
    });

    const updated = mergeMessageUpdate(messages, {
      id: "assistant-message-1",
      role: "assistant",
      sessionID: "session-1",
      time: { created: 1000, completed: 2000 },
      parts: [
        { id: "assistant-text-1", type: "text", text: "Lifecycle text that replaces the shorter part update." },
      ],
    });

    expect(updated.filter((message) => message.messageId === "assistant-message-1")).toHaveLength(1);
    expect(updated.filter((message) => message.role === "user")).toHaveLength(1);
    expect(updated.filter((message) => message.role === "assistant")).toHaveLength(1);
    expect(updated.map((message) => message.role)).toEqual(["user", "assistant"]);
    expect(updated.map((message) => message.messageId)).toEqual(["user-message-1", "assistant-message-1"]);

    const assistant = updated.find((message) => message.messageId === "assistant-message-1");
    expect(assistant).toMatchObject({
      role: "assistant",
      createdAt: 1000,
      completedAt: 2000,
    });
    expect(assistant?.parts).toEqual([
      {
        partId: "assistant-text-1",
        type: "text",
        text: "Lifecycle text that replaces the shorter part update.",
      },
    ]);
  });

  it("reconciles_text_delta_placeholder_with_lifecycle_without_duplicating_final_snapshot_text", () => {
    const messages = applyTextDelta(
      [],
      "assistant-message-1",
      "assistant-text-1",
      "session-1",
      "Streamed response text.",
    );

    const created = mergeMessageUpdate(messages, {
      id: "assistant-message-1",
      role: "assistant",
      sessionID: "session-1",
      time: { created: 1000 },
      parts: [
        { id: "assistant-text-1", type: "text", text: "Streamed response text." },
      ],
    });
    const updated = mergeMessageUpdate(created, {
      id: "assistant-message-1",
      role: "assistant",
      sessionID: "session-1",
      time: { created: 1000, completed: 2000 },
      parts: [
        { id: "assistant-text-1", type: "text", text: "Streamed response text." },
      ],
    });

    expect(updated.filter((message) => message.messageId === "assistant-message-1")).toHaveLength(1);

    const assistant = updated.find((message) => message.messageId === "assistant-message-1");
    expect(assistant).toMatchObject({
      messageId: "assistant-message-1",
      sessionId: "session-1",
      role: "assistant",
      createdAt: 1000,
      completedAt: 2000,
    });
    expect(assistant?.parts).toEqual([
      {
        partId: "assistant-text-1",
        type: "text",
        text: "Streamed response text.",
      },
    ]);
  });

  it("preserves_longer_streamed_text_when_shorter_final_lifecycle_snapshot_arrives", () => {
    const messages = applyTextDelta(
      [],
      "assistant-message-1",
      "assistant-text-1",
      "session-1",
      "Streamed response text with additional delta words.",
    );

    const created = mergeMessageUpdate(messages, {
      id: "assistant-message-1",
      role: "assistant",
      sessionID: "session-1",
      time: { created: 1000 },
      parts: [
        { id: "assistant-text-1", type: "text", text: "Streamed response text with additional delta words." },
      ],
    });
    const updated = mergeMessageUpdate(created, {
      id: "assistant-message-1",
      role: "assistant",
      sessionID: "session-1",
      time: { created: 1000, completed: 2000 },
      parts: [
        { id: "assistant-text-1", type: "text", text: "Streamed response text." },
      ],
    });

    expect(updated.filter((message) => message.messageId === "assistant-message-1")).toHaveLength(1);

    const assistant = updated.find((message) => message.messageId === "assistant-message-1");
    expect(assistant).toMatchObject({
      role: "assistant",
      createdAt: 1000,
      completedAt: 2000,
    });
    expect(assistant?.parts).toEqual([
      {
        partId: "assistant-text-1",
        type: "text",
        text: "Streamed response text with additional delta words.",
      },
    ]);
  });

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
