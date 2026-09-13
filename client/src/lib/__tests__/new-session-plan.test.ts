import { describe, expect, it } from "vitest";
import { describeNewSession, tildePath, worktreeFolderLabel, type NewSessionPlanInput } from "@/lib/new-session-plan";

function sentence(overrides: Partial<NewSessionPlanInput>): string {
  return describeNewSession({
    folder: { kind: "repository", path: "/home/me/src/rocket" },
    workspace: { kind: "new" },
    currentBranch: null,
    newBranch: undefined,
    existingBranch: null,
    ...overrides,
  }).map((part) => part.text).join("");
}

describe("tildePath", () => {
  it.each([
    ["/home/me/src/rocket", "~/src/rocket"],
    ["/Users/me/src/rocket", "~/src/rocket"],
    ["C:\\Users\\me\\src\\rocket", "~\\src\\rocket"],
    ["/home/me", "~"],
    ["/srv/repos/rocket", "/srv/repos/rocket"],
    ["/home/me-too", "~"],
  ])("%s → %s", (path, expected) => {
    expect(tildePath(path)).toBe(expected);
  });
});

describe("worktreeFolderLabel", () => {
  it("names the folder the server will create", () => {
    expect(worktreeFolderLabel("/home/me/src/rocket", "fleet/fix-login")).toBe("rocket-worktrees/fleet-fix-login");
  });
});

describe("describeNewSession", () => {
  it("asks for a folder when none is chosen", () => {
    expect(sentence({ folder: null })).toBe("Choose where it runs.");
  });

  it("chat only", () => {
    expect(sentence({ folder: { kind: "none" } })).toBe("Chat only. No folder.");
  });

  it("a folder as it is", () => {
    expect(sentence({ folder: { kind: "directory", path: "/home/me/notes" } })).toBe("Runs in ~/notes as it is.");
  });

  it("the current checkout, with its branch when known", () => {
    expect(sentence({ workspace: { kind: "current" }, currentBranch: "feature/x" }))
      .toBe("Works directly in ~/src/rocket on feature/x. Edits land in your checkout.");
    expect(sentence({ workspace: { kind: "current" } }))
      .toBe("Works directly in ~/src/rocket. Edits land in your checkout.");
  });

  it("a new worktree on the branch it will get", () => {
    expect(sentence({ newBranch: "fleet/fix-login" }))
      .toBe("New worktree rocket-worktrees/fleet-fix-login on fleet/fix-login, from the default branch.");
  });

  it("a new worktree before there's a message", () => {
    expect(sentence({ newBranch: undefined }))
      .toBe("New worktree from the default branch. The branch is named from your message.");
  });

  it("an existing worktree", () => {
    expect(sentence({
      workspace: { kind: "existing", path: "/home/me/src/rocket-worktrees/fix-login" },
      existingBranch: "fleet/fix-login",
    })).toBe("Continues in ~/src/rocket-worktrees/fix-login on fleet/fix-login.");
  });

  it("marks paths and branches as code", () => {
    const parts = describeNewSession({
      folder: { kind: "repository", path: "/home/me/src/rocket" },
      workspace: { kind: "new" },
      currentBranch: null,
      newBranch: "fleet/fix-login",
      existingBranch: null,
    });

    expect(parts.filter((part) => part.code).map((part) => part.text))
      .toEqual(["rocket-worktrees/fleet-fix-login", "fleet/fix-login"]);
  });
});
