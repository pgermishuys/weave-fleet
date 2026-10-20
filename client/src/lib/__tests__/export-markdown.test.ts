import { describe, expect, it } from "vitest";
import type { AccumulatedMessage } from "@/lib/client-types";
import {
  DEFAULT_EXPORT_OPTIONS,
  buildMarkdown,
  exportFileName,
  findKeyLines,
  hasReasoning,
  type ExportContext,
  type ExportOptions,
} from "@/lib/export-markdown";

const at = (hour: number, minute: number) => new Date(2026, 9, 20, hour, minute).getTime();

const context: ExportContext = {
  title: "Add upload retries",
  harness: "opencode2",
  model: "claude-sonnet-5-5",
  directory: "/home/sam/work/harbor-api",
  exportedAt: new Date(2026, 9, 20, 10, 14),
};

const messages: AccumulatedMessage[] = [
  {
    messageId: "m1", sessionId: "s", role: "user", createdAt: at(10, 2),
    parts: [{ partId: "p0", type: "text", text: "Uploads fail when the network blips. Add retries." }],
  },
  {
    messageId: "m2", sessionId: "s", role: "assistant", createdAt: at(10, 2), modelID: "claude-sonnet-5-5",
    parts: [
      { partId: "p1", type: "reasoning", text: "upload.ts calls put() once.\nWrap it." },
      { partId: "p2", type: "text", text: "I'll read the upload code first." },
      {
        partId: "p3", type: "tool", tool: "read", callId: "c1",
        state: { status: "completed", input: { filePath: "/home/sam/work/harbor-api/src/upload.ts" }, output: "export async function upload() {}" },
      },
      {
        partId: "p4", type: "tool", tool: "edit", callId: "c2",
        state: {
          status: "completed",
          input: { filePath: "/home/sam/work/harbor-api/src/upload.ts" },
          output: "Edit applied.",
          metadata: { diff: "@@ -1 +1,2 @@\n-old\n+new\n+more" },
        },
      },
      {
        partId: "p5", type: "tool", tool: "bash", callId: "c3",
        state: {
          status: "completed",
          input: { command: "bun test upload", description: "Run the upload tests" },
          output: "2 pass\nS3_TOKEN=sk_live_9f3a7c51d0e84b22a6\n\u001b[32mok\u001b[0m",
        },
      },
      { partId: "p6", type: "tool", tool: "bash", callId: "c4", state: { status: "error", input: { command: "false" }, output: "boom" } },
      {
        partId: "p7", type: "tool", tool: "fleet_page_show", callId: "c5",
        state: { status: "completed", input: { title: "Retry timeline", path: "/home/sam/work/harbor-api/t.html" } },
      },
    ],
  },
  {
    messageId: "m3", sessionId: "s", role: "user", createdAt: at(10, 9),
    parts: [
      { partId: "p8", type: "text", text: "Show me the waits." },
      { partId: "p9", type: "file", mime: "image/png", filename: "shot.png", url: "data:image/png;base64,AAAA" },
    ],
  },
  {
    messageId: "m4", sessionId: "s", role: "assistant", createdAt: at(10, 9), modelID: "claude-sonnet-5-5",
    parts: [{ partId: "p10", type: "compaction", tokensBefore: 181000, tokensAfter: 34000 }, { partId: "p11", type: "text", text: "Waits are 200 ms then 400 ms." }],
  },
  { messageId: "m5", sessionId: "s", role: "assistant", compactionSummary: true, parts: [{ partId: "p12", type: "text", text: "SUMMARY BODY" }] },
];

const build = (options: Partial<ExportOptions> = {}) =>
  buildMarkdown(messages, { ...DEFAULT_EXPORT_OPTIONS, ...options }, context);

describe("buildMarkdown", () => {
  it("writes the default export: tools as one line, no thinking, short paths, times and model", () => {
    expect(build().markdown).toBe(`# Add upload retries

_opencode2 · claude-sonnet-5-5 · exported 2026-10-20 10:14_

## You · 10:02

Uploads fail when the network blips. Add retries.

## Assistant · 10:02 · claude-sonnet-5-5

I'll read the upload code first.

- Read \`src/upload.ts\`
- Edit \`src/upload.ts\`
- Bash \`bun test upload\`
- Bash \`false\` (failed)

> Showed page: "Retry timeline"

## You · 10:09

Show me the waits.

[image: shot.png]

---

_Context compacted · 181k → 34k tokens_

## Assistant · 10:09 · claude-sonnet-5-5

Waits are 200 ms then 400 ms.
`);
  });

  it("never carries file bytes and leaves out a compaction's summary message", () => {
    const { markdown } = build({ tools: "output", thinking: true });
    expect(markdown).not.toContain("base64");
    expect(markdown).not.toContain("SUMMARY BODY");
  });

  it("leaves tool calls out, but keeps the page the agent showed", () => {
    const { markdown } = build({ tools: "off" });
    expect(markdown).not.toContain("Read `");
    expect(markdown).not.toContain("(failed)");
    expect(markdown).toContain('> Showed page: "Retry timeline"');
  });

  it("adds each call's output, a diff for an edit and a text block for a command, with tool output on", () => {
    const { markdown } = build({ tools: "output" });
    expect(markdown).toContain("- Read `src/upload.ts`\n\n```\nexport async function upload() {}\n```");
    expect(markdown).toContain("```diff\n@@ -1 +1,2 @@\n-old\n+new\n+more\n```");
    expect(markdown).toContain("```text\n2 pass\nS3_TOKEN=sk_live_9f3a7c51d0e84b22a6\nok\n```");
  });

  it("quotes thinking when asked", () => {
    expect(build().markdown).not.toContain("Thinking");
    expect(build({ thinking: true }).markdown).toContain("> **Thinking**\n>\n> upload.ts calls put() once.\n> Wrap it.");
  });

  it("keeps full paths and drops times and model when those are off", () => {
    const { markdown } = build({ shortenPaths: false, meta: false });
    expect(markdown).toContain("- Read `/home/sam/work/harbor-api/src/upload.ts`");
    expect(markdown).toContain("## You\n");
    expect(markdown).not.toContain("exported");
    expect(markdown).not.toContain("10:02");
  });

  it("counts the lines that look like keys, and rewrites nothing", () => {
    expect(build().keyLines).toBe(0);
    const withOutput = build({ tools: "output" });
    expect(withOutput.keyLines).toBe(1);
    expect(withOutput.markdown).toContain("sk_live_9f3a7c51d0e84b22a6");
  });

  it("counts the turns that were written", () => {
    expect(build().turns).toBe(4);
  });

  it("opens a fence longer than any backticks in the output", () => {
    const tricky: AccumulatedMessage[] = [{
      messageId: "x", sessionId: "s", role: "assistant",
      parts: [{ partId: "p", type: "tool", tool: "bash", callId: "c", state: { status: "completed", input: { command: "echo" }, output: "```\ninner\n```" } }],
    }];
    const { markdown } = buildMarkdown(tricky, { ...DEFAULT_EXPORT_OPTIONS, tools: "output" }, context);
    expect(markdown).toContain("````text\n```\ninner\n```\n````");
  });

  it("cuts a very long output and says so", () => {
    const long: AccumulatedMessage[] = [{
      messageId: "x", sessionId: "s", role: "assistant",
      parts: [{ partId: "p", type: "tool", tool: "bash", callId: "c", state: { status: "completed", input: { command: "build" }, output: "a".repeat(25_000) } }],
    }];
    const { markdown } = buildMarkdown(long, { ...DEFAULT_EXPORT_OPTIONS, tools: "output" }, context);
    expect(markdown).toContain("… 5000 more characters left out");
  });

  it("finds an edit's diff in OpenCode 2's per-file patches, or builds one from the swapped strings", () => {
    const edit = (state: Record<string, unknown>): AccumulatedMessage[] => [{
      messageId: "x", sessionId: "s", role: "assistant",
      parts: [{ partId: "p", type: "tool", tool: "edit", callId: "c", state: { status: "completed", output: "Edit applied.", ...state } }],
    }];
    const output = { ...DEFAULT_EXPORT_OPTIONS, tools: "output" as const };

    const patched = buildMarkdown(edit({ metadata: { files: [{ file: "a.ts", patch: "@@ -1 +1 @@\n-a\n+b\n" }] } }), output, context);
    expect(patched.markdown).toContain("```diff\n@@ -1 +1 @@\n-a\n+b\n```");

    const swapped = buildMarkdown(edit({ input: { filePath: "a.ts", oldString: "a", newString: "b\nc" } }), output, context);
    expect(swapped.markdown).toContain("```diff\n-a\n+b\n+c\n```");
  });

  it("writes an agent's steps, which arrive as messages of their own, as one turn", () => {
    const step = (id: string, text: string): AccumulatedMessage => ({
      messageId: id, sessionId: "s", role: "assistant", createdAt: at(10, 2), parts: [{ partId: id, type: "text", text }],
    });
    const { markdown, turns } = buildMarkdown([step("a", "One."), step("b", "Two.")], { ...DEFAULT_EXPORT_OPTIONS, meta: false }, context);
    expect(markdown).toContain("## Assistant\n\nOne.\n\nTwo.\n");
    expect(turns).toBe(1);
  });

  it("keeps a long path whole", () => {
    const long = "/home/sam/work/harbor-api/packages/uploads/src/retry/backoff-policy-with-a-long-name.ts";
    const read: AccumulatedMessage[] = [{
      messageId: "x", sessionId: "s", role: "assistant",
      parts: [{ partId: "p", type: "tool", tool: "read", callId: "c", state: { status: "completed", input: { path: long } } }],
    }];
    expect(buildMarkdown(read, DEFAULT_EXPORT_OPTIONS, context).markdown).toContain("`packages/uploads/src/retry/backoff-policy-with-a-long-name.ts`");
  });

  it("shows a slash command as the command, not its expanded template", () => {
    const command: AccumulatedMessage[] = [{
      messageId: "x", sessionId: "s", role: "user",
      command: { name: "review", arguments: "main" },
      parts: [{ partId: "p", type: "text", text: "Long expanded template…" }],
    }];
    const { markdown } = buildMarkdown(command, DEFAULT_EXPORT_OPTIONS, context);
    expect(markdown).toContain("`/review main`");
    expect(markdown).not.toContain("template");
  });
});

describe("hasReasoning", () => {
  it("is true only when a message has reasoning with text", () => {
    expect(hasReasoning(messages)).toBe(true);
    expect(hasReasoning(messages.slice(0, 1))).toBe(false);
  });
});

describe("findKeyLines", () => {
  it("recognises common key shapes and ignores plain text", () => {
    expect(findKeyLines("hello\nAKIAABCDEFGHIJKLMNOP\nghp_" + "a".repeat(24) + "\nPASSWORD=hunter2hunter2\nall fine")).toBe(3);
  });
});

describe("exportFileName", () => {
  it("names the file after the title", () => {
    expect(exportFileName("Add upload retries!", "ses_1")).toBe("add-upload-retries.md");
    expect(exportFileName("???", "ses_1")).toBe("ses_1.md");
  });
});
