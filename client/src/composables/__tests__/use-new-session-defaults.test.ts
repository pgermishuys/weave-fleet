import { beforeEach, describe, expect, it } from "vitest";
import type { ScannedRepository, WorktreeInfo } from "@/api/client";
import { mountComposable } from "@/composables/__tests__/test-utils";
import { NEW_SESSION_DEFAULTS_KEY, useNewSessionDefaults } from "@/composables/use-new-session-defaults";

const rocket: ScannedRepository = { name: "rocket", path: "/src/rocket", parentRoot: "/src" };
const comet: ScannedRepository = { name: "comet", path: "/src/comet", parentRoot: "/src" };
const repositories = [rocket, comet];

const loginWorktree: WorktreeInfo = { path: "/src/rocket-worktrees/fix-login", branch: "fleet/fix-login", commitHash: "abc" };

async function mountDefaults() {
  const { result } = await mountComposable(() => useNewSessionDefaults());
  return result;
}

beforeEach(() => {
  localStorage.clear();
});

describe("useNewSessionDefaults", () => {
  describe("first use", () => {
    it("has no folder and no recent folders", async () => {
      const defaults = await mountDefaults();

      expect(defaults.initialFolder(repositories)).toBeNull();
      expect(defaults.recentFolders(repositories)).toEqual([]);
    });

    it("starts a repository on a new worktree", async () => {
      const defaults = await mountDefaults();

      expect(defaults.workspaceFor(rocket.path, [])).toEqual({ kind: "new" });
      expect(defaults.lastWorktreeFor(rocket.path)).toBeNull();
    });
  });

  describe("after creating sessions", () => {
    it("starts with the last folder", async () => {
      const defaults = await mountDefaults();

      defaults.remember({ kind: "repository", path: rocket.path }, { kind: "new" });
      expect(defaults.initialFolder(repositories)).toEqual({ kind: "repository", path: rocket.path });

      defaults.remember({ kind: "directory", path: "/tmp/notes" }, { kind: "new" });
      expect(defaults.initialFolder(repositories)).toEqual({ kind: "directory", path: "/tmp/notes" });

      defaults.remember({ kind: "none" }, { kind: "new" });
      expect(defaults.initialFolder(repositories)).toEqual({ kind: "none" });
    });

    it("lists recent folders newest first, once each, without chats", async () => {
      const defaults = await mountDefaults();

      defaults.remember({ kind: "repository", path: rocket.path }, { kind: "new" });
      defaults.remember({ kind: "directory", path: "/tmp/notes" }, { kind: "current" });
      defaults.remember({ kind: "none" }, { kind: "new" });
      defaults.remember({ kind: "repository", path: rocket.path }, { kind: "new" });

      expect(defaults.recentFolders(repositories)).toEqual([
        { kind: "repository", path: rocket.path },
        { kind: "directory", path: "/tmp/notes" },
      ]);
    });

    it("keeps at most five recent folders", async () => {
      const defaults = await mountDefaults();

      for (let index = 0; index < 7; index++) {
        defaults.remember({ kind: "directory", path: `/tmp/${index}` }, { kind: "current" });
      }

      expect(defaults.recentFolders(repositories).map((folder) => folder.kind === "directory" && folder.path))
        .toEqual(["/tmp/6", "/tmp/5", "/tmp/4", "/tmp/3", "/tmp/2"]);
    });

    it("remembers the workspace per repository", async () => {
      const defaults = await mountDefaults();

      defaults.remember({ kind: "repository", path: rocket.path }, { kind: "existing", path: loginWorktree.path });
      defaults.remember({ kind: "repository", path: comet.path }, { kind: "current" });

      expect(defaults.workspaceFor(rocket.path, [loginWorktree])).toEqual({ kind: "existing", path: loginWorktree.path });
      expect(defaults.lastWorktreeFor(rocket.path)).toBe(loginWorktree.path);
      expect(defaults.workspaceFor(comet.path, [])).toEqual({ kind: "current" });
      expect(defaults.lastWorktreeFor(comet.path)).toBeNull();
    });

    it("is shared by every page that uses it", async () => {
      const first = await mountDefaults();
      first.remember({ kind: "repository", path: comet.path }, { kind: "current" });

      const second = await mountDefaults();

      expect(second.initialFolder(repositories)).toEqual({ kind: "repository", path: comet.path });
      expect(second.workspaceFor(comet.path, [])).toEqual({ kind: "current" });
    });
  });

  describe("stale entries", () => {
    it("falls back to the most recent repository that still exists", async () => {
      const defaults = await mountDefaults();
      defaults.remember({ kind: "repository", path: comet.path }, { kind: "new" });
      defaults.remember({ kind: "repository", path: "/src/deleted" }, { kind: "new" });

      expect(defaults.initialFolder(repositories)).toEqual({ kind: "repository", path: comet.path });
      expect(defaults.recentFolders(repositories)).toEqual([{ kind: "repository", path: comet.path }]);
    });

    it("falls back to nothing when no remembered repository exists", async () => {
      const defaults = await mountDefaults();
      defaults.remember({ kind: "repository", path: "/src/deleted" }, { kind: "new" });

      expect(defaults.initialFolder(repositories)).toBeNull();
    });

    it("falls back to a new worktree when the remembered worktree is gone", async () => {
      const defaults = await mountDefaults();
      defaults.remember({ kind: "repository", path: rocket.path }, { kind: "existing", path: loginWorktree.path });

      expect(defaults.workspaceFor(rocket.path, [])).toEqual({ kind: "new" });
    });

    it("keeps the remembered worktree while worktrees are loading", async () => {
      const defaults = await mountDefaults();
      defaults.remember({ kind: "repository", path: rocket.path }, { kind: "existing", path: loginWorktree.path });

      expect(defaults.workspaceFor(rocket.path, null)).toEqual({ kind: "existing", path: loginWorktree.path });
    });

    it("ignores storage it can't read", async () => {
      localStorage.setItem(NEW_SESSION_DEFAULTS_KEY, JSON.stringify({
        lastFolder: { kind: "repository" },
        recentFolders: "nope",
        workspaceByRepository: { [rocket.path]: 42 },
      }));
      const defaults = await mountDefaults();

      expect(defaults.initialFolder(repositories)).toBeNull();
      expect(defaults.recentFolders(repositories)).toEqual([]);
      expect(defaults.workspaceFor(rocket.path, [])).toEqual({ kind: "new" });

      defaults.remember({ kind: "repository", path: rocket.path }, { kind: "current" });
      expect(defaults.workspaceFor(rocket.path, [])).toEqual({ kind: "current" });
    });

    it("ignores storage that isn't JSON", async () => {
      localStorage.setItem(NEW_SESSION_DEFAULTS_KEY, "{not json");
      const defaults = await mountDefaults();

      expect(defaults.initialFolder(repositories)).toBeNull();
    });
  });
});
