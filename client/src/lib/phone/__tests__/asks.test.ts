import { describe, expect, it } from "vitest";
import { dontAskAgain, permissionTitle } from "../asks";

describe("permission ask words", () => {
  it("heads the ask with what the agent wants, as the desktop card does", () => {
    expect(permissionTitle({ kind: "shell", tool: "bash" })).toBe("Run a command");
    expect(permissionTitle({ kind: "edit", tool: "edit" })).toBe("Edit a file");
    expect(permissionTitle({ kind: "web", tool: "webfetch" })).toBe("Go online");
    expect(permissionTitle({ kind: "other", tool: "external_directory" })).toBe("Work outside the folder");
    expect(permissionTitle({ kind: "other", tool: "webfetch" })).toBe("Use webfetch");
  });

  it("says what Don't ask again covers: the patterns, or the whole kind of thing", () => {
    expect(dontAskAgain({ kind: "shell", tool: "bash", always: ["dotnet test *"] })).toEqual({ lead: "Don't ask again for", code: "dotnet test *" });
    expect(dontAskAgain({ kind: "shell", tool: "bash", always: ["git status", "git diff *"] })).toEqual({ lead: "Don't ask again for", code: "git status, git diff *" });
    expect(dontAskAgain({ kind: "shell", tool: "bash", always: ["*"] })).toEqual({ lead: "Don't ask again for commands", code: null });
    expect(dontAskAgain({ kind: "edit", tool: "edit", always: [] })).toEqual({ lead: "Don't ask again for file edits", code: null });
    expect(dontAskAgain({ kind: "other", tool: "mcp_x", always: [] })).toEqual({ lead: "Don't ask again for", code: "mcp_x" });
  });
});
