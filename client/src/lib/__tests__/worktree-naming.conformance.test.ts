import { readFileSync } from "node:fs";
import { dirname, join } from "node:path";
import { fileURLToPath } from "node:url";
import { describe, expect, it } from "vitest";
import {
  defaultWorktreeNaming,
  resolveWorktreeName,
  type WorktreeNamingContext,
  type WorktreeNamingTemplates,
} from "@/lib/worktree-naming";

/**
 * The shared cases the server's resolver is asserted against too
 * (WorktreeNameResolverConformanceTests.cs). A case the two disagree on is a name the composer
 * promised and the worktree didn't get.
 */

interface NamingCase {
  name: string;
  naming: Partial<WorktreeNamingTemplates>;
  message: string;
  branchOverride?: string;
  expect: { branch: string | null; root: string; folder: string };
}

interface Fixture {
  context: {
    repositoryPath: string;
    user: string;
    prefix: string;
    date: string;
    shortId: string;
    home: string;
  };
  cases: NamingCase[];
}

const fixturePath = join(
  dirname(fileURLToPath(import.meta.url)),
  "../../../../tests/contracts/worktree-naming-cases.json",
);
const fixture = JSON.parse(readFileSync(fixturePath, "utf8")) as Fixture;

const context: WorktreeNamingContext = {
  repositoryPath: fixture.context.repositoryPath,
  user: fixture.context.user,
  date: fixture.context.date,
  shortId: fixture.context.shortId,
  home: fixture.context.home,
};

describe("worktree naming conformance", () => {
  it("has cases to run", () => {
    expect(fixture.cases.length).toBeGreaterThan(0);
  });

  for (const namingCase of fixture.cases) {
    it(namingCase.name, () => {
      const naming: WorktreeNamingTemplates = {
        ...defaultWorktreeNaming,
        // The fixture's own prefix, unless the case sets one — including to empty.
        prefix: fixture.context.prefix,
        ...namingCase.naming,
      };

      const result = resolveWorktreeName(
        naming,
        context,
        namingCase.message,
        namingCase.branchOverride,
      );

      expect(result.branch).toBe(namingCase.expect.branch);
      expect(result.root).toBe(namingCase.expect.root);
      expect(result.folder).toBe(namingCase.expect.folder);
    });
  }
});

describe("worktree naming preview", () => {
  it("leaves an empty message's branch to the server when there's no short id to stand in", () => {
    // The composer doesn't know the session id, so it can't promise "fleet/session-<id>".
    const result = resolveWorktreeName(defaultWorktreeNaming, { ...context, shortId: "" }, "");

    expect(result.branch).toBeNull();
  });
});
