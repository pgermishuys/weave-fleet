import { describe, expect, it } from "vitest";
import { defaultNewFolderRoot, folderNameFrom, joinPath, parseCloneSource, rootContaining } from "@/lib/new-folder";

describe("parseCloneSource", () => {
  it.each([
    ["pgermishuys/recipe-box", "pgermishuys/recipe-box", "recipe-box"],
    ["pgermishuys/recipe-box.git", "pgermishuys/recipe-box", "recipe-box"],
    ["github.com/pgermishuys/recipe-box", "pgermishuys/recipe-box", "recipe-box"],
    ["https://github.com/pgermishuys/recipe-box/", "pgermishuys/recipe-box", "recipe-box"],
    ["https://github.com/pgermishuys/recipe-box.git", "pgermishuys/recipe-box", "recipe-box"],
    ["https://github.com/pgermishuys/recipe-box/tree/main/src", "pgermishuys/recipe-box", "recipe-box"],
  ])("reads %s as the GitHub repository %s", (typed, repository, name) => {
    expect(parseCloneSource(typed)).toEqual({ repository, label: repository, name });
  });

  it.each([
    ["https://gitlab.com/group/sub/project.git", "project"],
    ["ssh://git@example.com/team/repo.git", "repo"],
    ["git@github.com:pgermishuys/recipe-box.git", "recipe-box"],
  ])("keeps the address %s as typed", (typed, name) => {
    expect(parseCloneSource(typed)).toEqual({ repository: typed, label: typed, name });
  });

  it.each(["recipe-box", "recipe box", "/home/me/src/weave", "~/src/weave", "file:///etc", ""])(
    "reads %s as something else",
    (typed) => {
      expect(parseCloneSource(typed)).toBeNull();
    },
  );
});

describe("folderNameFrom", () => {
  it("keeps case, turns spaces into dashes, and drops characters folders can't have", () => {
    expect(folderNameFrom("  Recipe Box ")).toBe("Recipe-Box");
    expect(folderNameFrom('what: "a" <test>?')).toBe("what-a-test");
    expect(folderNameFrom("..hidden")).toBe("hidden");
    expect(folderNameFrom("   ")).toBe("");
  });
});

describe("joinPath", () => {
  it("uses the separator the parent uses", () => {
    expect(joinPath("/home/me/src", "recipe-box")).toBe("/home/me/src/recipe-box");
    expect(joinPath("/home/me/src/", "recipe-box")).toBe("/home/me/src/recipe-box");
    expect(joinPath("C:\\source", "recipe-box")).toBe("C:\\source\\recipe-box");
  });
});

describe("where a new folder goes", () => {
  const roots = ["/home/me/src", "/home/me/work"];

  it("finds the root a path is in, and not one that only shares a prefix", () => {
    expect(rootContaining("/home/me/work/billing", roots)).toBe("/home/me/work");
    expect(rootContaining("/home/me/workshop/x", roots)).toBeNull();
  });

  it("prefers the root used last, then the root of the folder in use, then the first", () => {
    expect(defaultNewFolderRoot(roots, "/home/me/work", "/home/me/src/rocket")).toBe("/home/me/work");
    expect(defaultNewFolderRoot(roots, "/gone", "/home/me/work/billing")).toBe("/home/me/work");
    expect(defaultNewFolderRoot(roots, null, null)).toBe("/home/me/src");
    expect(defaultNewFolderRoot([], null, null)).toBeNull();
  });
});
