import { describe, expect, it } from "vitest";
import type { AccumulatedToolPart } from "@/lib/client-types";
import { isSubagentTool, toToolCardItem } from "@/components/session/activity-stream-tool-card";
import { toRunningWorkItem, type RunningWorkItem } from "@/lib/running-work";

function work(extra: Record<string, unknown>): RunningWorkItem {
  return toRunningWorkItem({
    id: "w1", sessionId: "s1", workId: "b7x", kind: "shell", title: "Bash", label: "sh scripts/dev.sh", status: "running",
    background: true, toolCallId: "call-1", canStop: true, startedAt: "2026-10-04T10:00:00Z", ...extra,
  })!;
}

function create_tool_part(state: unknown, tool = "bash"): AccumulatedToolPart {
  return {
    partId: "part-1",
    type: "tool",
    tool,
    callId: "call-1",
    state,
  };
}

describe("toToolCardItem diff", () => {
  it("shows the unified diff kept on the call's metadata, with its line numbers", () => {
    const item = toToolCardItem(create_tool_part({
      status: "completed",
      input: { filePath: "/work/calc.py", oldString: "def sub(a, b):", newString: "def subtract(a, b):" },
      metadata: { diff: "--- /work/calc.py\n+++ /work/calc.py\n@@ -2,4 +2,4 @@\n     return a + b\n \n-def sub(a, b):\n+def subtract(a, b):\n" },
    }, "edit"));

    expect(item.diffLines?.map((line) => line.type)).toEqual(["context", "context", "remove", "add"]);
    expect(item.diffLines?.[2]).toMatchObject({ content: "def sub(a, b):", oldLineNumber: 4 });
    expect(item.diffLines?.[3]).toMatchObject({ content: "def subtract(a, b):", newLineNumber: 4 });
  });

  it("prefers diff lines the harness attached", () => {
    const item = toToolCardItem(create_tool_part({
      status: "completed",
      diffLines: [{ type: "add", content: "x" }],
      metadata: { diff: "@@ -1 +1 @@\n-a\n+b\n" },
    }, "edit"));

    expect(item.diffLines).toEqual([expect.objectContaining({ type: "add", content: "x" })]);
  });
});

describe("toToolCardItem", () => {
  // Claude Code reports a call that started background work done at once; the work says it still runs.
  it("draws a call whose work runs on in the background as Background, whatever the call says", () => {
    const part = create_tool_part({ status: "completed", input: { command: "sh scripts/dev.sh", run_in_background: true } }, "Bash");

    expect(toToolCardItem(part).status).toBe("Completed");
    expect(toToolCardItem(part, undefined, work({})).status).toBe("Background");
    // Foreground work (a subagent the call waits on) is the call's own running.
    expect(toToolCardItem(part, undefined, work({ background: false })).status).toBe("Completed");
    // Finished on its own: the call's own state says it.
    expect(toToolCardItem(part, undefined, work({ status: "completed", endedAt: "2026-10-04T10:05:00Z", endedReason: "completed" })).status)
      .toBe("Completed");
  });

  it("says Stopped for work Fleet stopped, on every harness", () => {
    const claudeCall = create_tool_part({ status: "completed", input: { command: "sh scripts/dev.sh" } }, "Bash");
    const stopped = work({ status: "cancelled", endedAt: "2026-10-04T10:05:00Z", endedReason: "cancelled" });
    // OpenCode 2's call stays running with the handle; its notice says how it ended.
    const openCodeCall = create_tool_part({ status: "running", background: true, metadata: { shellID: "sh_1" } }, "shell");

    expect(toToolCardItem(claudeCall, undefined, stopped).status).toBe("Stopped");
    expect(toToolCardItem(openCodeCall, new Map([["sh_1", "cancelled"]])).status).toBe("Stopped");
  });

  it("shows a browser tool's title and address, and the canvas it opened", () => {
    const done = toToolCardItem(create_tool_part({
      status: "completed",
      input: { command: "npm run dev", title: "Shop" },
      title: "Shop · http://localhost:5173/",
      output: "Running `npm run dev` (app_1) in /work/shop.",
      metadata: { canvasId: "cv_1", version: 2 },
    }, "fleet_app_start"));
    const running = toToolCardItem(create_tool_part({ status: "running", input: { command: "npm run dev", title: "Shop" } }, "fleet_app_start"));

    expect(done).toMatchObject({ title: "Shop · http://localhost:5173/", canvasId: "cv_1", kind: "fleet_app_start" });
    expect(running).toMatchObject({ title: "Shop · npm run dev", canvasId: undefined });
  });

  it("shows a saved note as the note itself, whatever the harness says the call was", () => {
    const done = toToolCardItem(create_tool_part({
      status: "completed",
      input: { text: "WebFetch can't read github.com pages; use gh.", list: "machine", kind: "learned" },
      title: "Remembered for this machine",
      output: "WebFetch can't read github.com pages; use gh.\nSaved as note 1a2b3c4d for this machine. Sessions read it from their next request.",
    }, "fleet_memory_save"));
    const running = toToolCardItem(create_tool_part({ status: "running", input: { text: "WebFetch can't read github.com pages; use gh." } }, "fleet_memory_save"));

    expect(done).toMatchObject({ title: "WebFetch can't read github.com pages; use gh.", preview: "└ WebFetch can't read github.com pages; use gh. (2 lines)" });
    expect(running.title).toBe("WebFetch can't read github.com pages; use gh.");
  });

  it("points a screenshot call at the copy Fleet kept, under the session it names", () => {
    const shot = toToolCardItem(create_tool_part({
      status: "completed",
      input: { canvasId: "cv_1", path: "", viewport: "phone" },
      title: "Shop · 390×844",
      output: "Screenshot of http://localhost:5173/ at 390×844.",
      metadata: { canvasId: "cv_1", version: 2, screenshot: { sessionId: "ses parent", id: "shot_01J", width: 390, height: 844 } },
    }, "fleet_browser_screenshot"));

    expect(shot.screenshot).toEqual({ url: "/api/sessions/ses%20parent/screenshots/shot_01J", width: 390, height: 844 });
  });

  it("gives a call no screenshot when its metadata doesn't name a whole one", () => {
    const shotWith = (screenshot: unknown) => toToolCardItem(create_tool_part({
      status: "completed",
      metadata: { canvasId: "cv_1", screenshot },
    }, "fleet_browser_screenshot")).screenshot;

    expect(shotWith(undefined)).toBeUndefined();
    expect(shotWith({ sessionId: "ses-1", id: "shot_1" })).toBeUndefined();
    expect(shotWith({ sessionId: "ses-1", width: 1280, height: 800 })).toBeUndefined();
    expect(shotWith("shot_1")).toBeUndefined();
    expect(toToolCardItem(create_tool_part({ status: "running" }, "fleet_browser_screenshot")).screenshot).toBeUndefined();
  });

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
      initiallyCollapsed: true,
    });
    expect(item.diffLines).toEqual([]);
  });
});

describe("isSubagentTool", () => {
  it("links OpenCode's task and OpenCode 2's subagent calls to their child session", () => {
    expect(isSubagentTool("task")).toBe(true);
    expect(isSubagentTool("subagent")).toBe(true);
    // In any case: a harness that keeps its own capitalised name still links.
    expect(isSubagentTool("Task")).toBe(true);
    expect(isSubagentTool("shell")).toBe(false);
  });
});
