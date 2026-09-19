import { describe, expect, it } from "vitest";
import { getToolLabel } from "@/lib/tool-labels";
import { isBashTool } from "@/lib/pr-utils";
import { toToolCardItem } from "@/components/session/activity-stream-tool-card";
import type { AccumulatedToolPart } from "@/lib/client-types";

// OpenCode 2 renames tools (bash → shell, task → subagent) and names a file `path` where OpenCode says `filePath`.
// Each harness's names are read as they are; the server doesn't translate them.
describe("getToolLabel", () => {
  it("labels OpenCode's tools from their input", () => {
    expect(getToolLabel("bash", { command: "echo hi", description: "Say hi" })).toBe("Say hi");
    expect(getToolLabel("read", { filePath: "/repo/src/lib/tool-labels.ts" })).toBe("/repo/src/lib/tool-labels.ts");
    expect(getToolLabel("edit", { filePath: "/a/very/long/path/that/goes/on/and/on/for/a/while/src/lib/tool-labels.ts" }))
      .toBe("…/lib/tool-labels.ts");
    expect(getToolLabel("skill", { name: "fleet-run" })).toBe("fleet-run");
  });

  it("labels OpenCode 2's shell like OpenCode's bash", () => {
    expect(getToolLabel("shell", { command: "echo from-tool" })).toBe("echo from-tool");
    expect(getToolLabel("shell", { command: "echo from-tool", description: "Echo" })).toBe("Echo");
    expect(getToolLabel("shell", null)).toBe("shell");
  });

  it("reads the file OpenCode 2 names `path`", () => {
    expect(getToolLabel("read", { path: "/work/alpha/note.txt" })).toBe("/work/alpha/note.txt");
    expect(getToolLabel("edit", { path: "src/app.ts", oldString: "a", newString: "b" })).toBe("src/app.ts");
    expect(getToolLabel("write", { path: "src/new.ts", content: "" })).toBe("src/new.ts");
    expect(getToolLabel("read", {})).toBe("read");
  });

  it("labels OpenCode 2's other tools", () => {
    expect(getToolLabel("glob", { pattern: "**/*.ts" })).toBe("**/*.ts");
    expect(getToolLabel("grep", { pattern: "TODO", path: "src" })).toBe("TODO");
    expect(getToolLabel("webfetch", { url: "https://opencode.ai/v2/docs" })).toBe("https://opencode.ai/v2/docs");
    expect(getToolLabel("websearch", { query: "opencode 2 forms" })).toBe("opencode 2 forms");
    expect(getToolLabel("skill", { id: "fleet-run" })).toBe("fleet-run");
    expect(getToolLabel("subagent", { agent: "general", description: "Child work", prompt: "say hello" })).toBe("general · Child work");
    expect(getToolLabel("subagent", { agent: "general" })).toBe("general");
    expect(getToolLabel("question", { questions: [{ question: "Pick one", header: "Pick", options: [] }] })).toBe("Pick one");
    expect(getToolLabel("execute", { code: "\n  const files = await glob({ pattern: '*.ts' });\nreturn files;" }))
      .toBe("const files = await glob({ pattern: '*.ts' });");
  });

  it("falls back to the tool's name when the input says nothing", () => {
    for (const tool of ["websearch", "subagent", "question", "execute"]) {
      expect(getToolLabel(tool, null)).toBe(tool);
    }
  });
});

describe("a shell tool card", () => {
  it("shows OpenCode 2's command and output", () => {
    const part: AccumulatedToolPart = {
      partId: "msg_1-tool-call_1",
      type: "tool",
      tool: "shell",
      callId: "call_1",
      state: {
        status: "completed",
        input: { command: "echo from-tool", description: "Echo" },
        output: "from-tool\nCommand exited with code 0.",
        metadata: { exit: 0 },
      },
    };

    const card = toToolCardItem(part);

    expect(card.title).toBe("Echo");
    expect(card.status).toBe("Completed");
    expect(card.output).toBe("from-tool\nCommand exited with code 0.");
    expect(card.preview).toBe("└ from-tool (2 lines)");
  });
});

describe("isBashTool", () => {
  it("is the shell tool in both OpenCodes", () => {
    expect(isBashTool("bash")).toBe(true);
    expect(isBashTool("shell")).toBe(true);
    expect(isBashTool("read")).toBe(false);
  });
});
