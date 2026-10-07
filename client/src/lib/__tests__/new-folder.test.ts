import { describe, expect, it } from "vitest";
import {
  defaultNewFolderRoot,
  folderPathFrom,
  foldersBetween,
  isRootedPath,
  joinPath,
  parseCloneSource,
  rootContaining,
  separatorOf,
  splitTypedPath,
  withSeparator,
} from "@/lib/new-folder";

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

describe("folderPathFrom", () => {
  it("keeps case and turns spaces into dashes", () => {
    expect(folderPathFrom("  Recipe Box ")).toEqual({ segments: ["Recipe-Box"], invalid: null });
    expect(folderPathFrom("..hidden")).toEqual({ segments: ["hidden"], invalid: null });
    expect(folderPathFrom("   ")).toEqual({ segments: [], invalid: null });
  });

  it("reads / and \\ as folders inside folders, on any machine", () => {
    expect(folderPathFrom("clients\\acme portal").segments).toEqual(["clients", "acme-portal"]);
    expect(folderPathFrom("clients/acme-portal/").segments).toEqual(["clients", "acme-portal"]);
    // Never climbs out of the location.
    expect(folderPathFrom("../../etc").segments).toEqual(["etc"]);
  });

  it("names a character no folder can have instead of dropping it", () => {
    expect(folderPathFrom('what: "a"').invalid).toBe(":");
    expect(folderPathFrom("notes?").invalid).toBe("?");
  });
});

describe("the folder box", () => {
  it("splits what's typed into the folder to list and the name typed in it", () => {
    expect(splitTypedPath("~/src/clients/acme")).toEqual({ folder: "~/src/clients/", name: "acme" });
    expect(splitTypedPath("C:\\Users\\me\\source\\")).toEqual({ folder: "C:\\Users\\me\\source\\", name: "" });
    expect(splitTypedPath("acme")).toEqual({ folder: "", name: "acme" });
  });

  it("tells a path of its own from a name inside the location", () => {
    expect(["~", "~/src", "/srv", "C:\\source", "c:/source", "\\\\server\\share"].every(isRootedPath)).toBe(true);
    expect(["clients/acme", "acme", ".\\acme"].some(isRootedPath)).toBe(false);
  });

  it("writes separators the way the machine does", () => {
    expect(separatorOf("C:\\Users\\me\\source")).toBe("\\");
    expect(separatorOf("/home/me/src")).toBe("/");
    expect(separatorOf(null)).toBe("/");
    expect(withSeparator("clients/acme\\site", "\\")).toBe("clients\\acme\\site");
  });

  it("lists the folders a create would make between what's there and the target", () => {
    expect(foldersBetween("/home/me/src", "/home/me/src/clients/acme")).toEqual(["clients", "acme"]);
    expect(foldersBetween("C:\\source\\", "C:\\source\\acme")).toEqual(["acme"]);
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
