import { describe, expect, it } from "vitest";
import { applyPartUpdate, applyTextDelta, mergeMessageUpdate, ensureMessage } from "@/lib/event-state";

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
        messageId: "msg_0000010000000001_user",
        sessionId: "session-1",
        role: "user" as const,
        createdAt: 1000,
        parts: [
          { partId: "user-text-1", type: "text" as const, text: "Explain stream ordering." },
        ],
      },
      {
        messageId: "msg_0000010000000002_assistant",
        sessionId: "session-1",
        role: "assistant" as const,
        parts: [
          { partId: "assistant-text-1", type: "text" as const, text: "Partial response" },
        ],
      },
    ];

    const updated = mergeMessageUpdate(messages, {
      id: "msg_0000010000000002_assistant",
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
        messageId: "msg_0000010000000001_user",
        sessionId: "session-1",
        role: "user" as const,
        createdAt: 1000,
        parts: [
          { partId: "user-text-1", type: "text" as const, text: "Explain out-of-order message events." },
        ],
      },
    ], {
      messageID: "msg_0000010000000002_assistant",
      sessionID: "session-1",
      id: "assistant-text-1",
      type: "text",
      text: "Part update text that is longer than the lifecycle snapshot.",
    });

    const updated = mergeMessageUpdate(messages, {
      id: "msg_0000010000000002_assistant",
      role: "assistant",
      sessionID: "session-1",
      time: { created: 1000, completed: 2000 },
      parts: [
        { id: "assistant-text-1", type: "text", text: "Lifecycle text." },
      ],
    });

    expect(updated.filter((message) => message.messageId === "msg_0000010000000002_assistant")).toHaveLength(1);
    expect(updated.filter((message) => message.role === "user")).toHaveLength(1);
    expect(updated.filter((message) => message.role === "assistant")).toHaveLength(1);
    expect(updated.map((message) => message.role)).toEqual(["user", "assistant"]);
    expect(updated.map((message) => message.messageId)).toEqual(["msg_0000010000000001_user", "msg_0000010000000002_assistant"]);

    const assistant = updated.find((message) => message.messageId === "msg_0000010000000002_assistant");
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
        messageId: "msg_0000010000000001_user",
        sessionId: "session-1",
        role: "user" as const,
        createdAt: 1000,
        parts: [
          { partId: "user-text-1", type: "text" as const, text: "Explain out-of-order message events." },
        ],
      },
    ], {
      messageID: "msg_0000010000000002_assistant",
      sessionID: "session-1",
      id: "assistant-text-1",
      type: "text",
      text: "Partial.",
    });

    const updated = mergeMessageUpdate(messages, {
      id: "msg_0000010000000002_assistant",
      role: "assistant",
      sessionID: "session-1",
      time: { created: 1000, completed: 2000 },
      parts: [
        { id: "assistant-text-1", type: "text", text: "Lifecycle text that replaces the shorter part update." },
      ],
    });

    expect(updated.filter((message) => message.messageId === "msg_0000010000000002_assistant")).toHaveLength(1);
    expect(updated.filter((message) => message.role === "user")).toHaveLength(1);
    expect(updated.filter((message) => message.role === "assistant")).toHaveLength(1);
    expect(updated.map((message) => message.role)).toEqual(["user", "assistant"]);
    expect(updated.map((message) => message.messageId)).toEqual(["msg_0000010000000001_user", "msg_0000010000000002_assistant"]);

    const assistant = updated.find((message) => message.messageId === "msg_0000010000000002_assistant");
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
      "msg_0000010000000001_assistant",
      "assistant-text-1",
      "session-1",
      "Streamed response text.",
    );

    const created = mergeMessageUpdate(messages, {
      id: "msg_0000010000000001_assistant",
      role: "assistant",
      sessionID: "session-1",
      time: { created: 1000 },
      parts: [
        { id: "assistant-text-1", type: "text", text: "Streamed response text." },
      ],
    });
    const updated = mergeMessageUpdate(created, {
      id: "msg_0000010000000001_assistant",
      role: "assistant",
      sessionID: "session-1",
      time: { created: 1000, completed: 2000 },
      parts: [
        { id: "assistant-text-1", type: "text", text: "Streamed response text." },
      ],
    });

    expect(updated.filter((message) => message.messageId === "msg_0000010000000001_assistant")).toHaveLength(1);

    const assistant = updated.find((message) => message.messageId === "msg_0000010000000001_assistant");
    expect(assistant).toMatchObject({
      messageId: "msg_0000010000000001_assistant",
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
      "msg_0000010000000001_assistant",
      "assistant-text-1",
      "session-1",
      "Streamed response text with additional delta words.",
    );

    const created = mergeMessageUpdate(messages, {
      id: "msg_0000010000000001_assistant",
      role: "assistant",
      sessionID: "session-1",
      time: { created: 1000 },
      parts: [
        { id: "assistant-text-1", type: "text", text: "Streamed response text with additional delta words." },
      ],
    });
    const updated = mergeMessageUpdate(created, {
      id: "msg_0000010000000001_assistant",
      role: "assistant",
      sessionID: "session-1",
      time: { created: 1000, completed: 2000 },
      parts: [
        { id: "assistant-text-1", type: "text", text: "Streamed response text." },
      ],
    });

    expect(updated.filter((message) => message.messageId === "msg_0000010000000001_assistant")).toHaveLength(1);

    const assistant = updated.find((message) => message.messageId === "msg_0000010000000001_assistant");
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
        messageId: "msg_0000010000000001_user",
        sessionId: "session-1",
        role: "user" as const,
        parts: [
          { partId: "text-1", type: "text" as const, text: "hello" },
        ],
      },
    ];

    const updated = mergeMessageUpdate(messages, {
      id: "msg_0000010000000001_user",
      time: { completed: 1234 },
      parts: [
        { id: "text-1", type: "text", text: "hello" },
        { id: "file-1", type: "file", mime: "image/png", filename: "screenshot.png", url: "data:image/png;base64,abc" },
      ],
    });

    const msg = updated.find((m) => m.messageId === "msg_0000010000000001_user");
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
        messageId: "msg_0000010000000001_user",
        sessionId: "session-1",
        role: "user" as const,
        parts: [
          { partId: "text-1", type: "text" as const, text: "hello" },
          { partId: "file-1", type: "file" as const, mime: "image/png", filename: "screenshot.png", url: "data:image/png;base64,abc" },
        ],
      },
    ];

    const updated = mergeMessageUpdate(messages, {
      id: "msg_0000010000000001_user",
      time: { completed: 1234 },
      parts: [
        { id: "text-1", type: "text", text: "hello" },
      ],
    });

    const msg = updated.find((m) => m.messageId === "msg_0000010000000001_user");
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

describe("message ordering", () => {
  describe("ensureMessage", () => {
    it("inserts_messages_in_chronological_order_by_message_id", () => {
      // msg_ IDs with hex timestamps sort lexicographically in chronological order
      const messages = ensureMessage([], {
        id: "msg_000001a2b3c4d5e6_abc123",
        sessionID: "session-1",
        role: "user",
        time: { created: 1000 },
      });

      const updated = ensureMessage(messages, {
        id: "msg_0000010203040506_def456", // Earlier hex timestamp
        sessionID: "session-1",
        role: "assistant",
        time: { created: 500 },
      });

      expect(updated.map((m) => m.messageId)).toEqual([
        "msg_0000010203040506_def456",
        "msg_000001a2b3c4d5e6_abc123",
      ]);
    });

    it("preserves_lexicographic_order_for_sequential_ids", () => {
      const messages = ensureMessage([], {
        id: "msg_0000010000000001_aaa",
        sessionID: "session-1",
        role: "user",
        time: { created: 1000 },
      });

      const updated = ensureMessage(messages, {
        id: "msg_0000010000000002_bbb",
        sessionID: "session-1",
        role: "assistant",
        time: { created: 1000 },
      });

      expect(updated.map((m) => m.messageId)).toEqual([
        "msg_0000010000000001_aaa",
        "msg_0000010000000002_bbb",
      ]);
    });

    it("orders_messages_with_same_millisecond_by_counter", () => {
      // Two messages created in the same millisecond (1735689600000 = 0x0000019400000000)
      // but with different counters (0001 vs 0002)
      const messages = ensureMessage([], {
        id: "msg_00000194000000000002_second",
        sessionID: "session-1",
        role: "assistant",
        time: { created: 1735689600000 },
      });

      const updated = ensureMessage(messages, {
        id: "msg_00000194000000000001_first",
        sessionID: "session-1",
        role: "user",
        time: { created: 1735689600000 },
      });

      // Despite arriving out of order, they should be sorted by ID
      // (same timestamp, different counter)
      expect(updated.map((m) => m.messageId)).toEqual([
        "msg_00000194000000000001_first",
        "msg_00000194000000000002_second",
      ]);
    });

    it("places_messages_without_msg_prefix_at_the_end", () => {
      const messages = ensureMessage([], {
        id: "msg_0000010000000001_aaa",
        sessionID: "session-1",
        role: "user",
        time: { created: 1000 },
      });

      const updated = ensureMessage(messages, {
        id: "legacy-message-id",
        sessionID: "session-1",
        role: "assistant",
        time: { created: 500 }, // Earlier timestamp but no msg_ prefix
      });

      expect(updated.map((m) => m.messageId)).toEqual([
        "msg_0000010000000001_aaa",
        "legacy-message-id",
      ]);
    });

    it("handles_out_of_order_arrival_user_assistant_user_assistant", () => {
      // Simulate: messages arrive out of order but IDs determine final position
      let messages = ensureMessage([], {
        id: "msg_0000010000000001_user1",
        sessionID: "session-1",
        role: "user",
        time: { created: 1000 },
      });

      messages = ensureMessage(messages, {
        id: "msg_0000010000000003_user2",
        sessionID: "session-1",
        role: "user",
        time: { created: 3000 },
      });

      messages = ensureMessage(messages, {
        id: "msg_0000010000000002_agent1", // Should go between user1 and user2
        sessionID: "session-1",
        role: "assistant",
        time: { created: 2000 },
      });

      messages = ensureMessage(messages, {
        id: "msg_0000010000000004_agent2",
        sessionID: "session-1",
        role: "assistant",
        time: { created: 4000 },
      });

      expect(messages.map((m) => m.messageId)).toEqual([
        "msg_0000010000000001_user1",
        "msg_0000010000000002_agent1",
        "msg_0000010000000003_user2",
        "msg_0000010000000004_agent2",
      ]);
    });
  });

  describe("applyPartUpdate", () => {
    it("inserts_new_message_in_sorted_position_when_creating_from_part", () => {
      const messages = [
        {
          messageId: "msg_0000010000000001_aaa",
          sessionId: "session-1",
          role: "user" as const,
          parts: [],
          createdAt: 1000,
        },
        {
          messageId: "msg_0000010000000003_ccc",
          sessionId: "session-1",
          role: "assistant" as const,
          parts: [],
          createdAt: 3000,
        },
      ];

      // Part update for a message with ID that sorts between existing messages
      const updated = applyPartUpdate(messages, {
        messageID: "msg_0000010000000002_bbb",
        sessionID: "session-1",
        id: "part-1",
        type: "text",
        text: "hello",
      });

      expect(updated.map((m) => m.messageId)).toEqual([
        "msg_0000010000000001_aaa",
        "msg_0000010000000002_bbb",
        "msg_0000010000000003_ccc",
      ]);
    });
  });

  describe("applyTextDelta", () => {
    it("inserts_new_message_in_sorted_position_when_creating_from_delta", () => {
      const messages = [
        {
          messageId: "msg_0000010000000001_aaa",
          sessionId: "session-1",
          role: "user" as const,
          parts: [],
          createdAt: 1000,
        },
        {
          messageId: "msg_0000010000000003_ccc",
          sessionId: "session-1",
          role: "assistant" as const,
          parts: [],
          createdAt: 3000,
        },
      ];

      // Text delta for a new message with ID that sorts in the middle
      const updated = applyTextDelta(
        messages,
        "msg_0000010000000002_bbb",
        "part-1",
        "session-1",
        "hello"
      );

      expect(updated.map((m) => m.messageId)).toEqual([
        "msg_0000010000000001_aaa",
        "msg_0000010000000002_bbb",
        "msg_0000010000000003_ccc",
      ]);
    });
  });

  describe("mergeMessageUpdate", () => {
    it("does_not_re_sort_when_createdAt_is_backfilled", () => {
      // Message IDs determine position, not createdAt
      // No re-sorting needed when createdAt is backfilled
      // Initial state: agent1 is at the end because it arrived via part.updated (no msg_ prefix initially)
      const messages = [
        {
          messageId: "msg_0000010000000001_user1",
          sessionId: "session-1",
          role: "user" as const,
          parts: [],
          createdAt: 1000,
        },
        {
          messageId: "msg_0000010000000003_user2",
          sessionId: "session-1",
          role: "user" as const,
          parts: [],
          createdAt: 3000,
        },
        {
          messageId: "msg_0000010000000002_agent1",
          sessionId: "session-1",
          role: "assistant" as const,
          parts: [{ partId: "part-1", type: "text" as const, text: "response" }],
          // No createdAt yet
        },
      ];

      const updated = mergeMessageUpdate(messages, {
        id: "msg_0000010000000002_agent1",
        time: { created: 2000 },
      });

      // Position unchanged - mergeMessageUpdate doesn't re-sort
      // The message stays at the end even though its ID would sort it in the middle
      expect(updated.map((m) => m.messageId)).toEqual([
        "msg_0000010000000001_user1",
        "msg_0000010000000003_user2",
        "msg_0000010000000002_agent1",
      ]);
      expect(updated[2]!.createdAt).toBe(2000);
    });

    it("updates_message_metadata_without_changing_position", () => {
      const messages = [
        {
          messageId: "msg_0000010000000001_aaa",
          sessionId: "session-1",
          role: "user" as const,
          parts: [],
          createdAt: 1000,
        },
        {
          messageId: "msg_0000010000000002_bbb",
          sessionId: "session-1",
          role: "assistant" as const,
          parts: [],
          createdAt: 2000,
        },
      ];

      const updated = mergeMessageUpdate(messages, {
        id: "msg_0000010000000002_bbb",
        time: { created: 2000, completed: 3000 },
      });

      expect(updated.map((m) => m.messageId)).toEqual([
        "msg_0000010000000001_aaa",
        "msg_0000010000000002_bbb",
      ]);
      expect(updated[1]!.completedAt).toBe(3000);
    });

    it("maintains_id_based_order_regardless_of_timestamp", () => {
      const messages = [
        {
          messageId: "msg_0000010000000001_aaa",
          sessionId: "session-1",
          role: "user" as const,
          parts: [],
          createdAt: 1000,
        },
        {
          messageId: "msg_0000010000000002_bbb",
          sessionId: "session-1",
          role: "assistant" as const,
          parts: [],
          createdAt: 1000,
        },
        {
          messageId: "msg_0000010000000003_ccc",
          sessionId: "session-1",
          role: "assistant" as const,
          parts: [],
          // No createdAt
        },
      ];

      const updated = mergeMessageUpdate(messages, {
        id: "msg_0000010000000003_ccc",
        time: { created: 500 }, // Earlier timestamp doesn't change position
      });

      expect(updated.map((m) => m.messageId)).toEqual([
        "msg_0000010000000001_aaa",
        "msg_0000010000000002_bbb",
        "msg_0000010000000003_ccc",
      ]);
      expect(updated[2]!.createdAt).toBe(500);
    });
  });
});
