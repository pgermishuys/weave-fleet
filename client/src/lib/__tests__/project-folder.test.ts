import { describe, expect, it } from "vitest";
import type { ScannedRepository, SessionListItem } from "@/api/client";
import { projectFolder } from "@/lib/project-folder";

const repositories: ScannedRepository[] = [
  { name: "harbor", path: "/home/sam/code/harbor", parentRoot: "/home/sam/code" },
  { name: "lighthouse", path: "/home/sam/code/lighthouse", parentRoot: "/home/sam/code" },
];

function session(overrides: Partial<SessionListItem> & { created: number }): SessionListItem {
  const { created, ...rest } = overrides;
  return {
    projectId: "p-harbor",
    session: { id: `s-${created}`, title: "Session", time: { created, updated: created }, tags: [] },
    workspaceDirectory: "/home/sam/code/harbor",
    sourceDirectory: null,
    isolationStrategy: "existing",
    origin: null,
    ...rest,
  } as SessionListItem;
}

describe("projectFolder", () => {
  it("is the repository a worktree session of the project's newest session came from", () => {
    const sessions = [
      session({ created: 1, workspaceDirectory: "/home/sam/code/lighthouse" }),
      session({
        created: 2,
        isolationStrategy: "worktree",
        workspaceDirectory: "/home/sam/.weave/worktrees/harbor-fix",
        sourceDirectory: "/home/sam/code/harbor",
      }),
    ];

    expect(projectFolder(sessions, "p-harbor", repositories)).toEqual({ kind: "repository", path: "/home/sam/code/harbor" });
  });

  it("only looks at the project's own sessions", () => {
    const sessions = [
      session({ created: 1 }),
      session({ created: 2, projectId: "p-other", workspaceDirectory: "/home/sam/code/lighthouse" }),
    ];

    expect(projectFolder(sessions, "p-harbor", repositories)).toEqual({ kind: "repository", path: "/home/sam/code/harbor" });
  });

  it("finds the repository of a session in an existing worktree from where it came from", () => {
    const sessions = [
      session({
        created: 1,
        workspaceDirectory: "/home/sam/.weave/worktrees/lighthouse-docs",
        origin: { providerId: "builtin.repository", sourceType: "repository", resourceId: "/home/sam/code/lighthouse", resourceUrl: null, title: "lighthouse" },
      }),
    ];

    expect(projectFolder(sessions, "p-harbor", repositories)).toEqual({ kind: "repository", path: "/home/sam/code/lighthouse" });
  });

  it("matches a Windows folder whatever its case or slashes, and picks the scanned path", () => {
    const windowsRepositories: ScannedRepository[] = [{ name: "harbor", path: "C:\\code\\harbor", parentRoot: "C:\\code" }];
    const sessions = [session({ created: 1, workspaceDirectory: "c:/code/harbor/" })];

    expect(projectFolder(sessions, "p-harbor", windowsRepositories)).toEqual({ kind: "repository", path: "C:\\code\\harbor" });
  });

  it("is a plain folder a session ran in", () => {
    const sessions = [
      session({
        created: 1,
        workspaceDirectory: "/home/sam/notes",
        origin: { providerId: "builtin.local", sourceType: "directory", resourceId: "/home/sam/notes", resourceUrl: null, title: null },
      }),
    ];

    expect(projectFolder(sessions, "p-harbor", repositories)).toEqual({ kind: "directory", path: "/home/sam/notes" });
  });

  it("skips quick chats and repositories that are gone, for an older session's folder", () => {
    const sessions = [
      session({ created: 1 }),
      session({
        created: 2,
        isolationStrategy: "worktree",
        workspaceDirectory: "/home/sam/.weave/worktrees/old-fix",
        sourceDirectory: "/home/sam/code/removed",
      }),
      session({
        created: 3,
        workspaceDirectory: "/home/sam/.weave/quick-chat/abc",
        origin: { providerId: "builtin.quickchat", sourceType: "quick-chat", resourceId: null, resourceUrl: null, title: null },
      }),
    ];

    expect(projectFolder(sessions, "p-harbor", repositories)).toEqual({ kind: "repository", path: "/home/sam/code/harbor" });
  });

  it("is null for a project with no sessions, or none with a folder", () => {
    const quickChat = session({
      created: 1,
      origin: { providerId: "builtin.quickchat", sourceType: "quick-chat", resourceId: null, resourceUrl: null, title: null },
    });

    expect(projectFolder([], "p-harbor", repositories)).toBeNull();
    expect(projectFolder([quickChat], "p-harbor", repositories)).toBeNull();
  });
});
