import { describe, expect, it } from "vitest";
import * as lucide from "lucide-vue-next";
import { getToolIcon, getToolDisplayLabel } from "@/lib/tool-icons";
import { getToolLabel } from "@/lib/tool-labels";
import { askPreview, type InboxItem } from "@/lib/phone/inbox";
import {
  allTools,
  askPreviewWording,
  asRecord,
  dontAskAgainWording,
  findTool,
  getTool,
  parseMcpName,
  parseToolInput,
  permissionHeading,
  type PermissionKindName,
  type ToolCategory,
} from "@/lib/tools";

describe("lookup", () => {
  it("finds a tool by its name, an alias, and in any case", () => {
    for (const name of ["edit", "Edit", "EDIT", "multiedit", "MultiEdit", "strreplaceeditor"]) {
      expect(getTool(name).name).toBe("edit");
    }
    expect(getTool("apply_patch").name).toBe("patch");
    expect(getTool("applypatch").name).toBe("patch");
    expect(getTool("Apply-Patch").name).toBe("patch");
    expect(getTool("Agent").name).toBe("task");
    expect(getTool("Task").name).toBe("task");
    expect(getTool("terminal").name).toBe("shell");
    expect(getTool("TodoWrite").name).toBe("todowrite");
    expect(getTool("LS").name).toBe("list");
    expect(getTool("WebFetch").name).toBe("webfetch");
  });

  it("keeps the name as it arrived", () => {
    expect(getTool("MultiEdit").raw).toBe("MultiEdit");
    expect(getTool("MultiEdit").label({})).toBe("MultiEdit");
  });

  it("claims every name once", () => {
    const names = allTools().flatMap((d) => [d.name, ...(d.aliases ?? [])]).map((n) => n.toLowerCase().replace(/[^a-z0-9]/g, ""));
    expect(new Set(names).size).toBe(names.length);
  });

  it("resolves Fleet's own MCP server to Fleet's tools", () => {
    expect(getTool("mcp__fleet__fleet_page_show").name).toBe("fleet_page_show");
    expect(getTool("mcp__fleet__fleet_page_show").titledFromAnswer).toBe(true);
    expect(getTool("mcp__fleet__fleet_page_show").label({ title: "Panel", path: "a.html" })).toBe("Panel · a.html");
  });

  it("knows nothing of another MCP server's tools, but reads their names", () => {
    const tool = getTool("mcp__acme_orders__lookup_order");
    expect(tool.known).toBe(false);
    expect(tool.mcp).toEqual({ server: "acme_orders", tool: "lookup_order" });
    expect(tool.category).toBe("other");
    expect(tool.permissionKind).toBe("other");
    expect(tool.displayName).toBe("Lookup_order");
    expect(tool.heading).toBe("Mcp__acme_orders__lookup_order");
    expect(tool.label({ id: 1 })).toBe("mcp__acme_orders__lookup_order");
  });

  it("an MCP server cannot pose as a built-in tool", () => {
    expect(getTool("mcp__acme__read").known).toBe(false);
    expect(getTool("mcp__acme__bash").permissionKind).toBe("other");
  });

  it("parses MCP names", () => {
    expect(parseMcpName("mcp__fleet__fleet_page_show")).toEqual({ server: "fleet", tool: "fleet_page_show" });
    expect(parseMcpName("mcp__x")).toBeNull();
    expect(parseMcpName("mcp__x__")).toBeNull();
    expect(parseMcpName("read")).toBeNull();
  });

  it("falls back for a tool it does not know", () => {
    const tool = getTool("frobnicate");
    expect(tool.known).toBe(false);
    expect(findTool("frobnicate")).toBeUndefined();
    expect(tool.category).toBe("other");
    expect(tool.permissionKind).toBe("other");
    expect(tool.icon).toBe(lucide.Wrench);
    expect(tool.heading).toBe("Frobnicate");
    expect(tool.label({ command: "ls" })).toBe("frobnicate");
    expect(tool.fileWrite).toBeUndefined();
  });

  it("copes with no name at all", () => {
    expect(getTool("").heading).toBe("");
    expect(getTool(undefined).category).toBe("other");
    expect(getTool(null).permissionKind).toBe("other");
  });

  it("treats any other fleet_ tool as Fleet's", () => {
    const tool = getTool("fleet_canvas_open");
    expect(tool.known).toBe(false);
    expect(tool.category).toBe("fleet");
    expect(tool.permissionKind).toBe("read");
  });
});

describe("categories", () => {
  const expected: Record<ToolCategory, string[]> = {
    read: ["read", "list", "LS", "lsp", "NotebookRead"],
    search: ["glob", "grep", "codesearch", "websearch"],
    edit: ["edit", "write", "patch", "apply_patch", "multiedit", "notebookedit", "NotebookEdit", "strreplaceeditor", "move"],
    shell: ["bash", "Bash", "shell", "terminal", "execute", "BashOutput", "KillShell"],
    web: ["webfetch", "WebFetch"],
    subagent: ["task", "Task", "Agent", "subagent"],
    question: ["question"],
    plan: ["todowrite", "TodoWrite", "todoread"],
    skill: ["skill"],
    fleet: ["fleet_app_start", "fleet_message", "fleet_memory_save", "fleet_mod_write", "fleet_mod_list", "fleet_canvas_open"],
    other: ["frobnicate", "mcp__acme__lookup_order", ""],
  };

  for (const [category, names] of Object.entries(expected)) {
    it(`${category}: ${names.join(", ")}`, () => {
      for (const name of names) expect([name, getTool(name).category]).toEqual([name, category]);
    });
  }

  it("file writes: write and notebooks create, the other edits modify", () => {
    expect(getTool("write").fileWrite).toBe("create");
    expect(getTool("NotebookEdit").fileWrite).toBe("create");
    expect(getTool("edit").fileWrite).toBe("modify");
    expect(getTool("multiedit").fileWrite).toBe("modify");
    expect(getTool("apply_patch").fileWrite).toBe("modify");
    expect(getTool("read").fileWrite).toBeUndefined();
  });

  it("flags the tools whose card title is Fleet's answer, and those that drive the browser", () => {
    const titled = allTools().filter((d) => d.titledFromAnswer).map((d) => d.name);
    expect(titled).toEqual([
      "fleet_app_start", "fleet_browser_open", "fleet_browser_screenshot", "fleet_page_show", "fleet_walkthrough_show",
      "fleet_message", "fleet_session_read", "fleet_machine_list", "fleet_session_start",
    ]);
    expect(allTools().filter((d) => d.usesBrowser).map((d) => d.name)).toEqual(["execute", "fleet_browser_read", "fleet_browser_act"]);
    expect(allTools().filter((d) => d.patternLabel).map((d) => d.name)).toEqual(["glob", "grep"]);
  });
});

describe("permission kinds follow the server (Permissions.Classify)", () => {
  // Copied from src/WeaveFleet.Domain/Harnesses/Permissions.cs, which compares case-insensitively.
  const server: Record<PermissionKindName, string[]> = {
    read: [
      "read", "glob", "grep", "list", "lsp", "codesearch", "todowrite", "todoread", "todo", "skill", "question", "task",
      "subagent", "opencode_list_mcp_resources", "opencode_read_mcp_resource",
      "LS", "NotebookRead", "Agent", "ExitPlanMode", "ListMcpResourcesTool", "ReadMcpResourceTool", "fleet_page_show", "fleet_whatever",
    ],
    edit: ["edit", "write", "patch", "apply_patch", "multiedit", "move", "NotebookEdit"],
    shell: ["bash", "shell", "execute", "BashOutput", "KillShell", "KillBash", "Bash"],
    web: ["webfetch", "websearch", "WebFetch"],
    other: ["frobnicate", "external_directory", "", "doom_loop"],
  };

  for (const [kind, names] of Object.entries(server)) {
    it(`${kind}: ${names.join(", ")}`, () => {
      for (const name of names) expect([name, getTool(name).permissionKind]).toEqual([name, kind]);
    });
  }

  it("decides the two names the client knows and the server does not", () => {
    expect(getTool("terminal").permissionKind).toBe("shell");
    expect(getTool("strreplaceeditor").permissionKind).toBe("edit");
  });
});

describe("same answers as the sites it will replace", () => {
  const inputs: (Record<string, unknown> | null)[] = [
    null,
    {},
    { filePath: "src/widgets/panel.ts" },
    { path: "/home/someone/a/very/long/directory/name/that/keeps/going/on/and/on/file.ts" },
    { command: "dotnet test --filter Something", description: "Run the tests" },
    { command: "x".repeat(90) },
    { pattern: "**/*.ts" },
    { url: "https://example.test/docs", query: "reconnect backoff" },
    { name: "fleet-run" },
    { id: "fleet-simplify" },
    { agent: "explore", description: "Look around" },
    { agent: "explore" },
    { questions: [{ question: "Which folder?" }] },
    { questions: [{ header: "Folder" }] },
    { code: "\n  await page.open()\nmore" },
    { title: "Panel", command: "bun run dev" },
    { title: "Panel", url: "http://localhost:3000" },
    { title: "Panel", path: "/tmp/mockups/settings/options.html" },
    { viewport: "phone", path: "/settings" },
    { what: "find", text: "Reconnect" },
    { action: "click", ref: "@e3" },
    { action: "fill", url: "", ref: "", text: "hello" },
    { text: "Prefer the scratch Fleet for UI work." },
    { id: "note-7" },
  ];

  // Everything the old switch labelled, and the old maps drew, under the lowercase name the server sends.
  const known = [
    "bash", "shell", "read", "edit", "write", "glob", "grep", "fleet_app_start", "fleet_browser_open", "fleet_page_show",
    "fleet_walkthrough_show", "fleet_browser_screenshot", "fleet_browser_read", "fleet_browser_act", "fleet_memory_save",
    "fleet_memory_forget", "fleet_mod_write", "fleet_mod_check", "fleet_mod_reload", "fleet_mod_test", "fleet_mod_keep",
    "fleet_mod_list", "webfetch", "skill", "websearch", "subagent", "question", "execute",
  ];

  it("labels every known tool as tool-labels does", () => {
    for (const name of known) {
      for (const input of inputs) {
        expect([name, input, getTool(name).label(input)]).toEqual([name, input, getToolLabel(name, input)]);
      }
    }
  });

  it("falls back to the tool name where tool-labels has no case", () => {
    for (const name of ["task", "list", "frobnicate", "fleet_message"]) {
      expect(getTool(name).label({ description: "x" })).toBe(getToolLabel(name, { description: "x" }));
      expect(getTool(name).label({ description: "x" })).toBe(name);
    }
  });

  it("labels the tools that take a file or folder by it, whichever field the harness used", () => {
    expect(getTool("list").label({ path: "src" })).toBe("src");
    expect(getTool("notebookedit").label({ notebook_path: "notebooks/demo.ipynb" })).toBe("notebooks/demo.ipynb");
    expect(getTool("patch").label({ filePath: "src/a.ts" })).toBe("src/a.ts");
    expect(getTool("lsp").label({ filePath: "src/a.ts" })).toBe("src/a.ts");
    expect(getTool("list").label({})).toBe("list");
  });

  it("draws every tool the icon map knows with the same icon and header", () => {
    const iconKnown = [
      "read", "write", "edit", "glob", "grep", "skill", "bash", "task", "webfetch", "question", "fleet_app_start",
      "fleet_browser_open", "fleet_browser_screenshot", "fleet_browser_read", "fleet_browser_act", "fleet_page_show",
      "fleet_walkthrough_show", "fleet_message", "fleet_session_read", "fleet_machine_list", "fleet_session_start",
      "fleet_memory_save", "fleet_memory_forget", "fleet_mod_write", "fleet_mod_check", "fleet_mod_reload", "fleet_mod_test",
      "fleet_mod_keep", "fleet_mod_list", "fleet_step_done", "shell", "subagent", "websearch", "execute",
    ];
    for (const name of iconKnown) {
      expect([name, getTool(name).icon]).toEqual([name, getToolIcon(name)]);
      expect([name, getTool(name).heading]).toEqual([name, getToolDisplayLabel(name)]);
    }
  });

  it("gives the headers a tool without an entry gets today", () => {
    for (const name of ["frobnicate", "list", "lsp", "codesearch", "TodoWrite", "mcp__acme__lookup_order", "Read"]) {
      // The icon map is exact-case; the registry is not, so only the unknown names must match.
      if (!getTool(name).known) expect(getTool(name).heading).toBe(getToolDisplayLabel(name));
    }
    expect(getToolDisplayLabel("list")).toBe(getTool("list").heading);
    expect(getToolDisplayLabel("lsp")).toBe(getTool("lsp").heading);
    expect(getToolDisplayLabel("codesearch")).toBe(getTool("codesearch").heading);
  });

  it("words permission asks as the phone does", () => {
    const kinds: PermissionKindName[] = ["shell", "edit", "read", "web", "other"];
    for (const kind of kinds) {
      for (const tool of ["bash", "external_directory", "mcp__acme__lookup_order"]) {
        for (const always of [[], ["*"], ["dotnet test *", "*"]]) {
          const ask = { kind, tool, always, title: "the thing" } as const;
          expect(permissionHeading(ask)).toBe(
            { shell: "Run a command", edit: "Edit a file", read: "Read a file", web: "Go online", other: tool === "external_directory" ? "Work outside the folder" : `Use ${tool}` }[kind],
          );
          expect(dontAskAgainWording(ask).lead).toMatch(/^Don't ask again for/);
          const entry = { status: "waiting_input", ask: { kind: "permission", ask: { ...ask, id: "p", sessionId: "s", askedAt: "" } } } as unknown as InboxItem;
          expect(askPreviewWording(ask)).toEqual(askPreview(entry));
        }
      }
    }
    expect(askPreviewWording({ kind: "other", tool: "mystery" })).toEqual({ lead: "Wants to use mystery", detail: null, code: false });
  });
});

describe("todowrite (the bug the registry fixes)", () => {
  it("has a label, a header and a todo-list icon", () => {
    const tool = getTool("todowrite");
    expect(tool.icon).toBe(lucide.ListTodo);
    expect(tool.heading).toBe("Update todos");
    expect(tool.label(null)).toBe("todowrite");
    expect(tool.label({ todos: [{ content: "a" }, { content: "b" }] })).toBe("2 todos");
    expect(tool.label({ todos: [{ content: "a" }] })).toBe("1 todo");
    expect(getTool("TodoWrite").icon).toBe(lucide.ListTodo);
    expect(getTool("todoread").icon).toBe(lucide.ListChecks);
  });
});

describe("parseToolInput", () => {
  it("reads the file under any harness's name", () => {
    expect(parseToolInput({ filePath: "a.ts" }).filePath).toBe("a.ts");
    expect(parseToolInput({ path: "b.ts" }).filePath).toBe("b.ts");
    expect(parseToolInput({ file_path: "c.ts" }).filePath).toBe("c.ts");
    expect(parseToolInput({ notebook_path: "d.ipynb" }).filePath).toBe("d.ipynb");
    expect(parseToolInput({ filePath: "", path: "b.ts" }).filePath).toBe("b.ts");
  });

  it("reads the common fields and ignores blanks and wrong types", () => {
    const input = parseToolInput({
      command: "ls", description: "  ", pattern: 3, url: "https://example.test", query: "q", name: "fleet-run", agent: "explore",
      content: "one\ntwo", old_string: "a", newString: "b", code: "run()",
    });
    expect(input).toMatchObject({
      command: "ls", description: undefined, pattern: undefined, url: "https://example.test", query: "q", skill: "fleet-run",
      agent: "explore", content: "one\ntwo", oldString: "a", newString: "b", code: "run()",
    });
  });

  it("takes a skill's id when it has no name", () => {
    expect(parseToolInput({ id: "fleet-design" }).skill).toBe("fleet-design");
  });

  it("reads questions", () => {
    expect(parseToolInput({ questions: [{ question: "Which?", header: "Pick" }, "nope", { header: "Only" }] }).questions).toEqual([
      { question: "Which?", header: "Pick" },
      { question: undefined, header: "Only" },
    ]);
    expect(parseToolInput({ questions: "x" }).questions).toEqual([]);
  });

  it("copes with anything that is not a record", () => {
    for (const value of [null, undefined, "x", 4, [], [{ command: "ls" }]]) {
      const input = parseToolInput(value);
      expect(input.command).toBeUndefined();
      expect(input.questions).toEqual([]);
      expect(input.raw).toEqual({});
    }
  });

  it("asRecord accepts only plain objects", () => {
    expect(asRecord({ a: 1 })).toEqual({ a: 1 });
    expect(asRecord([])).toBeNull();
    expect(asRecord(null)).toBeNull();
    expect(asRecord("x")).toBeNull();
  });
});
