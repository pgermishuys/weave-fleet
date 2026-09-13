import { describe, expect, it } from "vitest";
import { createGitHubSessionSourcePreset } from "@/lib/github-session-source";
import {
  branchForMessage,
  buildCreateSessionRequest,
  buildCreatedSessionRow,
  slugForBranch,
  titleFromMessage,
  type NewSessionState,
} from "@/lib/new-session-request";

const REPO = "/home/me/source/rocket";
const WORKTREE = "/home/me/source/rocket-worktrees/fix-login";

function state(overrides: Partial<NewSessionState>): NewSessionState {
  return {
    folder: { kind: "repository", path: REPO },
    workspace: { kind: "new" },
    message: "",
    ...overrides,
  };
}

function build(overrides: Partial<NewSessionState>) {
  const result = buildCreateSessionRequest(state(overrides));
  if (!result.ok) {
    throw new Error(`Expected a request but got: ${result.error}`);
  }
  return result;
}

const issue = createGitHubSessionSourcePreset({
  sourceType: "github-issue",
  owner: "acme",
  repo: "rocket",
  number: 42,
  title: "Login redirect loops",
  body: "Steps to reproduce…",
  htmlUrl: "https://github.com/acme/rocket/issues/42",
  repoFullName: "acme/rocket",
  suggestedBranch: "fix/issue-42",
});

describe("slugForBranch", () => {
  it("drops filler words and joins the rest with hyphens", () => {
    expect(slugForBranch("Can you fix the login redirect?")).toBe("fix-login-redirect");
  });

  it("turns punctuation into word breaks and keeps contractions whole", () => {
    expect(slugForBranch("Don't cache /api/users (v2)!!")).toBe("dont-cache-api-users-v2");
  });

  it("drops emoji and accents", () => {
    expect(slugForBranch("🚀 Déploy the café page 🎉")).toBe("deploy-cafe-page");
  });

  it("cuts long messages at a word boundary within 40 characters", () => {
    const slug = slugForBranch("Refactor the authentication middleware so tokens refresh before they expire");
    expect(slug).toBe("refactor-authentication-middleware");
    expect(slug.length).toBeLessThanOrEqual(40);
  });

  it("cuts a single word longer than the limit", () => {
    expect(slugForBranch("a".repeat(60))).toBe("a".repeat(40));
  });

  it("uses only the first non-empty line", () => {
    expect(slugForBranch("\n  \nAdd dark mode\nMore detail on the second line")).toBe("add-dark-mode");
  });

  it("keeps filler words when nothing else is left", () => {
    expect(slugForBranch("Can you do this?")).toBe("can-you-do-this");
  });

  it("is empty when nothing usable is left", () => {
    expect(slugForBranch("")).toBe("");
    expect(slugForBranch("   ")).toBe("");
    expect(slugForBranch("🎉🎉 !!")).toBe("");
  });
});

describe("branchForMessage", () => {
  it("prefixes the slug with fleet/", () => {
    expect(branchForMessage("Fix login redirect")).toBe("fleet/fix-login-redirect");
  });

  it("is undefined when the message gives no slug", () => {
    expect(branchForMessage("🎉")).toBeUndefined();
  });
});

describe("titleFromMessage", () => {
  it("is the first non-empty line, spaces tidied", () => {
    expect(titleFromMessage("\n  Fix the   login redirect \nMore detail")).toBe("Fix the login redirect");
  });

  it("cuts a long line at a word boundary", () => {
    const title = titleFromMessage("Refactor the authentication middleware so that tokens refresh before they expire");

    expect(title).toBe("Refactor the authentication middleware so that tokens…");
    expect(title.length).toBeLessThanOrEqual(60);
  });

  it("cuts one very long word", () => {
    expect(titleFromMessage("x".repeat(80))).toBe(`${"x".repeat(59)}…`);
  });

  it("is empty for an empty message", () => {
    expect(titleFromMessage(" \n ")).toBe("");
  });
});

describe("buildCreateSessionRequest", () => {
  describe("repository", () => {
    it("new worktree: branch from the message, sent as the first message", () => {
      const request = build({ message: "Fix the login redirect" });

      expect(request.directory).toBe(REPO);
      expect(request.options).toEqual({
        source: {
          key: { providerId: "builtin.repository", sourceType: "repository", actionId: "start-session", contractVersion: 1 },
          input: { repositoryPath: REPO, isolationStrategy: "worktree", branch: "fleet/fix-login-redirect" },
        },
        isolationStrategy: "worktree",
        branch: "fleet/fix-login-redirect",
        title: "Fix the login redirect",
        initialPrompt: "Fix the login redirect",
      });
    });

    it("new worktree without a message: no branch, so the server names one", () => {
      const request = build({ message: "" });

      expect(request.options.branch).toBeUndefined();
      expect(request.options.source?.input).toEqual({ repositoryPath: REPO, isolationStrategy: "worktree" });
      expect(request.options.initialPrompt).toBeUndefined();
    });

    it("new worktree: a typed branch wins over the message", () => {
      const request = build({ message: "Fix the login redirect", branch: " feature/login " });

      expect(request.options.branch).toBe("feature/login");
      expect(request.options.source?.input).toMatchObject({ branch: "feature/login" });
    });

    it("current checkout: works in the repository, no branch", () => {
      const request = build({ workspace: { kind: "current" }, message: "Fix the login redirect" });

      expect(request.directory).toBe(REPO);
      expect(request.options.isolationStrategy).toBe("existing");
      expect(request.options.branch).toBeUndefined();
      expect(request.options.source?.input).toEqual({ repositoryPath: REPO, isolationStrategy: "existing" });
    });

    it("existing worktree: sends its path and no branch", () => {
      const request = build({ workspace: { kind: "existing", path: WORKTREE }, message: "Carry on" });

      expect(request.options.isolationStrategy).toBe("worktree");
      expect(request.options.branch).toBeUndefined();
      expect(request.options.source?.input).toEqual({
        repositoryPath: REPO,
        isolationStrategy: "worktree",
        existingWorktreePath: WORKTREE,
      });
    });
  });

  describe("directory", () => {
    it.each([
      { kind: "current" } as const,
      { kind: "new" } as const,
      { kind: "existing", path: WORKTREE } as const,
    ])("works in the folder whatever the workspace ($kind)", (workspace) => {
      const request = build({ folder: { kind: "directory", path: " /tmp/scratch " }, workspace, message: "Hello there" });

      expect(request.directory).toBe("/tmp/scratch");
      expect(request.options.isolationStrategy).toBe("existing");
      expect(request.options.branch).toBeUndefined();
      expect(request.options.source).toEqual({
        key: { providerId: "builtin.local", sourceType: "directory", actionId: "start-session", contractVersion: 1 },
        input: { directory: "/tmp/scratch", isolationStrategy: "existing" },
      });
    });

    it("needs a path", () => {
      expect(buildCreateSessionRequest(state({ folder: { kind: "directory", path: "  " } })))
        .toEqual({ ok: false, error: "Pick a folder." });
    });
  });

  describe("no folder", () => {
    it.each([
      { kind: "current" } as const,
      { kind: "new" } as const,
      { kind: "existing", path: WORKTREE } as const,
    ])("starts a quick chat whatever the workspace ($kind)", (workspace) => {
      const request = build({ folder: { kind: "none" }, workspace, message: "What is a monad?" });

      expect(request.directory).toBeUndefined();
      expect(request.options.isolationStrategy).toBe("existing");
      expect(request.options.branch).toBeUndefined();
      expect(request.options.initialPrompt).toBe("What is a monad?");
      expect(request.options.source).toEqual({
        key: { providerId: "builtin.quickchat", sourceType: "quick-chat", actionId: "start-session", contractVersion: 1 },
        input: {},
      });
    });
  });

  describe("GitHub preset", () => {
    it("new worktree on the suggested branch, titled after the issue", () => {
      const request = build({ gitHubPreset: issue, message: "Start with the tests" });

      expect(request.options.branch).toBe("fix/issue-42");
      expect(request.options.title).toBe("Login redirect loops");
      expect(request.options.initialPrompt).toBe("Start with the tests");
      expect(request.options.source).toEqual({
        key: { providerId: "builtin.github", sourceType: "github-issue", actionId: "start-session", contractVersion: 1 },
        input: {
          owner: "acme",
          repo: "rocket",
          number: 42,
          repositoryPath: REPO,
          isolationStrategy: "worktree",
          branch: "fix/issue-42",
        },
      });
    });

    it("without a suggested branch, names the branch after the message", () => {
      const request = build({ gitHubPreset: { ...issue, suggestedBranch: null }, message: "Start with the tests" });

      expect(request.options.branch).toBe("fleet/start-tests");
    });

    it("existing worktree: sends its path instead of a branch", () => {
      const request = build({ gitHubPreset: issue, workspace: { kind: "existing", path: WORKTREE } });

      expect(request.options.branch).toBeUndefined();
      expect(request.options.source?.input).toEqual({
        owner: "acme",
        repo: "rocket",
        number: 42,
        repositoryPath: REPO,
        isolationStrategy: "worktree",
        existingWorktreePath: WORKTREE,
      });
    });

    it("current checkout", () => {
      const request = build({ gitHubPreset: issue, workspace: { kind: "current" } });

      expect(request.options.isolationStrategy).toBe("existing");
      expect(request.options.source?.input).toMatchObject({ isolationStrategy: "existing" });
      expect(request.options.source?.input).not.toHaveProperty("branch");
    });

    it("a typed title wins over the issue title", () => {
      expect(build({ gitHubPreset: issue, title: "My take" }).options.title).toBe("My take");
    });

    it.each([
      { kind: "none" } as const,
      { kind: "directory", path: "/tmp/scratch" } as const,
    ])("needs a repository ($kind)", (folder) => {
      expect(buildCreateSessionRequest(state({ gitHubPreset: issue, folder })))
        .toEqual({ ok: false, error: "Pick the repository for acme/rocket #42." });
    });
  });

  describe("options", () => {
    it("passes title, project, harness and cleaned tags", () => {
      const request = build({
        title: "  Login work ",
        projectId: "project-1",
        harnessType: "opencode",
        tags: [" review ", "", "deploy"],
      });

      expect(request.options).toMatchObject({
        title: "Login work",
        projectId: "project-1",
        harnessType: "opencode",
        tags: ["review", "deploy"],
      });
    });

    it("leaves out what wasn't chosen", () => {
      const request = build({ title: "  ", projectId: null, tags: ["  "] });

      expect(request.options).not.toHaveProperty("title");
      expect(request.options).not.toHaveProperty("projectId");
      expect(request.options).not.toHaveProperty("harnessType");
      expect(request.options).not.toHaveProperty("tags");
    });

    it("trims the message", () => {
      expect(build({ message: "\n  Fix it  \n" }).options.initialPrompt).toBe("Fix it");
    });

    it("titles the session after the message when no title is typed", () => {
      expect(build({ message: "Fix the login redirect\nIt loops on /login" }).options.title).toBe("Fix the login redirect");
      expect(build({ message: "Fix it", title: "Login work" }).options.title).toBe("Login work");
      expect(build({ message: "Fix it", gitHubPreset: issue }).options.title).toBe("Login redirect loops");
    });
  });
});

describe("buildCreatedSessionRow", () => {
  const response = {
    instanceId: "instance-1",
    workspaceId: "workspace-1",
    session: { id: "session-1", title: "Fix the login redirect", time: { created: 5, updated: 5 }, tags: ["review"] },
  };

  it("is a running, working session in the given project", () => {
    const row = buildCreatedSessionRow(response, build({ message: "Fix the login redirect" }), { id: "scratch", name: "Scratch" });

    expect(row).toMatchObject({
      instanceId: "instance-1",
      workspaceId: "workspace-1",
      session: response.session,
      sessionStatus: "active",
      activityStatus: "busy",
      lifecycleStatus: "running",
      retentionStatus: "active",
      isolationStrategy: "worktree",
      branch: "fleet/fix-login-redirect",
      projectId: "scratch",
      projectName: "Scratch",
      tags: ["review"],
    });
  });

  it("is idle when it started without a message", () => {
    const row = buildCreatedSessionRow(response, build({ message: "" }), null);

    expect(row.sessionStatus).toBe("idle");
    expect(row.projectId).toBeNull();
  });
});
