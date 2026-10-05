import { describe, expect, it } from "vitest";
import { alwaysCovers, permissionTitle, permissionWants } from "../asks";

describe("permission ask words", () => {
  it("heads the ask with what the agent wants", () => {
    expect(permissionTitle({ kind: "shell", tool: "bash" })).toBe("Run a command");
    expect(permissionTitle({ kind: "other", tool: "webfetch" } as never)).toBe("Use webfetch");
    expect(permissionWants({ kind: "edit", tool: "edit" })).toBe("Wants to edit a file");
  });

  it("reads a trailing * as starts-with, so dotnet test * shows as `dotnet test`", () => {
    expect(alwaysCovers({ kind: "shell", tool: "bash", always: ["dotnet test *"] })).toEqual({ lead: "Commands that start with", code: "dotnet test" });
    expect(alwaysCovers({ kind: "edit", tool: "edit", always: ["src/*"] })).toEqual({ lead: "Anything that starts with", code: "src/" });
    expect(alwaysCovers({ kind: "shell", tool: "bash", always: ["git status"] })).toEqual({ lead: "The command", code: "git status" });
    expect(alwaysCovers({ kind: "shell", tool: "bash", always: [] })).toEqual({ lead: "The command", code: "bash" });
  });
});
