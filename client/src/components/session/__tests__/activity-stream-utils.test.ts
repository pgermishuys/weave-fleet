import { describe, expect, it } from "vitest";
import type { AccumulatedToolPart } from "@/lib/api-types";
import { toToolCardItem } from "@/components/session/activity-stream-tool-card";

function create_tool_part(state: unknown, tool = "bash"): AccumulatedToolPart {
  return {
    partId: "part-1",
    type: "tool",
    tool,
    callId: "call-1",
    state,
  };
}

describe("toToolCardItem", () => {
  it("uses_result_as_output_when_output_is_absent", () => {
    const item = toToolCardItem(create_tool_part({
      status: "completed",
      result: "hello",
    }));

    expect(item.output).toBe("hello");
  });

  it("derives_title_status_summary_and_collapsed_state", () => {
    const item = toToolCardItem(create_tool_part({
      status: "completed",
      input: {
        description: "Lists files in current directory",
        command: "ls -la",
      },
      summary: "Applied patch",
      stdout: "file-a",
    }));

    expect(item).toMatchObject({
      id: "part-1",
      title: "Lists files in current directory",
      kind: "bash",
      status: "Completed",
      summary: "Applied patch",
      output: "file-a",
      initiallyCollapsed: true,
    });
  });

  it("falls_back_to_remaining_state_output_and_normalizes_diff_lines", () => {
    const item = toToolCardItem(create_tool_part({
      status: "completed",
      summary: "Applied patch",
      input: { command: "ignored in output" },
      diff: [
        { content: "+added line", newLineNumber: 3 },
        { line: "-removed line", oldLineNumber: 2 },
        { text: " unchanged line " },
      ],
      metadata: { changed: true },
    }, "custom-tool"));

    expect(item).toMatchObject({
      id: "part-1",
      title: "custom-tool",
      kind: "custom-tool",
      status: "Completed",
      summary: "Applied patch",
      initiallyCollapsed: true,
    });
    expect(item.output).toBe('{\n  "metadata": {\n    "changed": true\n  }\n}');
    expect(item.diffLines).toEqual([
      { type: "add", content: "+added line", oldLineNumber: undefined, newLineNumber: 3 },
      { type: "remove", content: "-removed line", oldLineNumber: 2, newLineNumber: undefined },
      { type: "context", content: " unchanged line ", oldLineNumber: undefined, newLineNumber: undefined },
    ]);
  });

  it("preserves_falsy_output_values_and_avoids_diff_only_json_fallback", () => {
    const zeroOutput = toToolCardItem(create_tool_part({
      status: "completed",
      output: 0,
    }));

    const falseOutput = toToolCardItem(create_tool_part({
      status: "completed",
      result: false,
    }));

    const diffOnly = toToolCardItem(create_tool_part({
      status: "completed",
      diff: [
        { content: "+added line", newLineNumber: 1 },
      ],
    }, "edit"));

    expect(zeroOutput.output).toBe("0");
    expect(falseOutput.output).toBe("false");
    expect(diffOnly.output).toBeUndefined();
    expect(diffOnly.diffLines).toEqual([
      { type: "add", content: "+added line", oldLineNumber: undefined, newLineNumber: 1 },
    ]);
  });

  it("returns_pending_defaults_when_state_is_not_a_record", () => {
    const item = toToolCardItem(create_tool_part(null, "read"));

    expect(item).toMatchObject({
      id: "part-1",
      title: "read",
      kind: "read",
      status: "Pending",
      output: undefined,
      initiallyCollapsed: false,
    });
    expect(item.diffLines).toEqual([]);
  });
});
