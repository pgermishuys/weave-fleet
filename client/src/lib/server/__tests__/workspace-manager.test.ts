import { mkdirSync, rmSync, existsSync, writeFileSync } from "fs";
import { tmpdir, homedir } from "os";
import { join, resolve } from "path";
import { randomUUID } from "crypto";
import { execSync } from "child_process";
import { _resetDbForTests } from "@/lib/server/database";
import { getWorkspace } from "@/lib/server/db-repository";
import {
  createWorkspace,
  cleanupWorkspace,
  getWorkspaceDirectory,
} from "@/lib/server/workspace-manager";
import { getProfileWorkspaceRoot } from "@/lib/server/profile";

// ─── Helpers ──────────────────────────────────────────────────────────────────

function makeTempDir(): string {
  const dir = join(tmpdir(), `weave-ws-test-${randomUUID()}`);
  mkdirSync(dir, { recursive: true });
  return dir;
}

function makeGitRepo(): string {
  const dir = makeTempDir();
  execSync("git init", { cwd: dir, stdio: "pipe" });
  execSync("git config user.email test@test.com", { cwd: dir, stdio: "pipe" });
  execSync("git config user.name Test", { cwd: dir, stdio: "pipe" });
  // Need at least one commit for worktrees to work
  writeFileSync(join(dir, "README.md"), "# Test repo");
  execSync("git add .", { cwd: dir, stdio: "pipe" });
  execSync('git commit -m "init"', { cwd: dir, stdio: "pipe" });
  return dir;
}

const testDirs: string[] = [];

function trackDir(dir: string): string {
  testDirs.push(dir);
  return dir;
}

// ─── Setup / Teardown ─────────────────────────────────────────────────────────

beforeEach(() => {
  process.env.WEAVE_DB_PATH = join(tmpdir(), `fleet-ws-test-${randomUUID()}.db`);
  process.env.WEAVE_WORKSPACE_ROOT = join(tmpdir(), `weave-ws-root-${randomUUID()}`);
  _resetDbForTests();
});

afterEach(() => {
  _resetDbForTests();
  delete process.env.WEAVE_DB_PATH;
  delete process.env.WEAVE_WORKSPACE_ROOT;
  delete process.env.WEAVE_PROFILE;
  for (const dir of testDirs.splice(0)) {
    rmSync(dir, { recursive: true, force: true });
  }
});

// ─── Strategy: existing ───────────────────────────────────────────────────────

describe("createWorkspace — existing strategy", () => {
  it("RecordsDirectoryInDbAndReturnsIt", async () => {
    const dir = trackDir(makeTempDir());
    const info = await createWorkspace({ sourceDirectory: dir, strategy: "existing" });
    expect(info.directory).toBe(resolve(dir));
    expect(info.strategy).toBe("existing");
    const ws = getWorkspace(info.id);
    expect(ws?.directory).toBe(resolve(dir));
    expect(ws?.isolation_strategy).toBe("existing");
  });

  it("DoesNotCopyOrCloneDirectory", async () => {
    const dir = trackDir(makeTempDir());
    const info = await createWorkspace({ sourceDirectory: dir, strategy: "existing" });
    // The returned directory IS the source directory (no copy)
    expect(info.directory).toBe(resolve(dir));
  });

  it("ThrowsForNonExistentDirectory", async () => {
    await expect(
      createWorkspace({
        sourceDirectory: "/tmp/__nonexistent_weave_test_xyz__",
        strategy: "existing",
      })
    ).rejects.toThrow("does not exist");
  });

  it("ThrowsForFilePath", async () => {
    const dir = trackDir(makeTempDir());
    const file = join(dir, "file.txt");
    writeFileSync(file, "test");
    await expect(
      createWorkspace({ sourceDirectory: file, strategy: "existing" })
    ).rejects.toThrow("is not a directory");
  });
});

// ─── Strategy: worktree ───────────────────────────────────────────────────────

describe("createWorkspace — worktree strategy", () => {
  it("CreatesGitWorktreeDirectory", async () => {
    const repo = trackDir(makeGitRepo());
    const info = await createWorkspace({ sourceDirectory: repo, strategy: "worktree" });
    trackDir(info.directory); // ensure cleanup
    expect(existsSync(info.directory)).toBe(true);
    expect(info.strategy).toBe("worktree");
  });

  it("WorktreeDirectoryIsValidGitCheckout", async () => {
    const repo = trackDir(makeGitRepo());
    const info = await createWorkspace({ sourceDirectory: repo, strategy: "worktree" });
    trackDir(info.directory);
    // Should be a git repo
    expect(() =>
      execSync("git rev-parse --git-dir", { cwd: info.directory, stdio: "pipe" })
    ).not.toThrow();
  });

  it("PersistsWorkspaceInDb", async () => {
    const repo = trackDir(makeGitRepo());
    const info = await createWorkspace({ sourceDirectory: repo, strategy: "worktree" });
    trackDir(info.directory);
    const ws = getWorkspace(info.id);
    expect(ws?.isolation_strategy).toBe("worktree");
    expect(ws?.source_directory).toBe(resolve(repo));
    expect(ws?.branch).toBeDefined();
  });

  it("UsesProvidedBranchName", async () => {
    const repo = trackDir(makeGitRepo());
    const info = await createWorkspace({
      sourceDirectory: repo,
      strategy: "worktree",
      branch: "feature/test-branch",
    });
    trackDir(info.directory);
    const ws = getWorkspace(info.id);
    expect(ws?.branch).toBe("feature/test-branch");
  });

  it("ThrowsForNonGitDirectory", async () => {
    const dir = trackDir(makeTempDir());
    await expect(
      createWorkspace({ sourceDirectory: dir, strategy: "worktree" })
    ).rejects.toThrow("not a git repository");
  });
});

// ─── Strategy: clone ──────────────────────────────────────────────────────────

describe("createWorkspace — clone strategy", () => {
  it("ClonesRepositoryToNewDirectory", async () => {
    const repo = trackDir(makeGitRepo());
    const info = await createWorkspace({ sourceDirectory: repo, strategy: "clone" });
    trackDir(info.directory);
    expect(existsSync(info.directory)).toBe(true);
    expect(info.strategy).toBe("clone");
  });

  it("ClonedDirectoryIsValidGitRepo", async () => {
    const repo = trackDir(makeGitRepo());
    const info = await createWorkspace({ sourceDirectory: repo, strategy: "clone" });
    trackDir(info.directory);
    expect(() =>
      execSync("git rev-parse --git-dir", { cwd: info.directory, stdio: "pipe" })
    ).not.toThrow();
  });

  it("PersistsCloneWorkspaceInDb", async () => {
    const repo = trackDir(makeGitRepo());
    const info = await createWorkspace({ sourceDirectory: repo, strategy: "clone" });
    trackDir(info.directory);
    const ws = getWorkspace(info.id);
    expect(ws?.isolation_strategy).toBe("clone");
    expect(ws?.source_directory).toBe(resolve(repo));
  });
});

// ─── Cleanup ──────────────────────────────────────────────────────────────────

describe("cleanupWorkspace", () => {
  it("ExistingStrategyDoesNotDeleteDirectory", async () => {
    const dir = trackDir(makeTempDir());
    const info = await createWorkspace({ sourceDirectory: dir, strategy: "existing" });
    await cleanupWorkspace(info.id);
    expect(existsSync(dir)).toBe(true);
    const ws = getWorkspace(info.id);
    expect(ws?.cleaned_up_at).not.toBeNull();
  });

  it("WorktreeStrategyRemovesWorktreeDirectory", async () => {
    const repo = trackDir(makeGitRepo());
    const info = await createWorkspace({ sourceDirectory: repo, strategy: "worktree" });
    expect(existsSync(info.directory)).toBe(true);
    await cleanupWorkspace(info.id);
    expect(existsSync(info.directory)).toBe(false);
    const ws = getWorkspace(info.id);
    expect(ws?.cleaned_up_at).not.toBeNull();
  });

  it("CloneStrategyDeletesClonesDirectory", async () => {
    const repo = trackDir(makeGitRepo());
    const info = await createWorkspace({ sourceDirectory: repo, strategy: "clone" });
    expect(existsSync(info.directory)).toBe(true);
    await cleanupWorkspace(info.id);
    expect(existsSync(info.directory)).toBe(false);
    const ws = getWorkspace(info.id);
    expect(ws?.cleaned_up_at).not.toBeNull();
  });

  it("IsIdempotentForAlreadyCleanedWorkspace", async () => {
    const dir = trackDir(makeTempDir());
    const info = await createWorkspace({ sourceDirectory: dir, strategy: "existing" });
    await cleanupWorkspace(info.id);
    await expect(cleanupWorkspace(info.id)).resolves.not.toThrow();
  });

  it("ThrowsForNonExistentWorkspaceId", async () => {
    await expect(cleanupWorkspace("nonexistent-id")).rejects.toThrow(
      "Workspace not found"
    );
  });
});

// ─── getWorkspaceDirectory ────────────────────────────────────────────────────

describe("getWorkspaceDirectory", () => {
  it("ReturnsWorkingDirectoryForExistingStrategy", async () => {
    const dir = trackDir(makeTempDir());
    const info = await createWorkspace({ sourceDirectory: dir, strategy: "existing" });
    expect(getWorkspaceDirectory(info.id)).toBe(resolve(dir));
  });

  it("ThrowsForNonExistentWorkspaceId", () => {
    expect(() => getWorkspaceDirectory("nonexistent")).toThrow("Workspace not found");
  });
});

// ─── Timeout & error behavior ─────────────────────────────────────────────────

describe("WEAVE_GIT_TIMEOUT_MS env var", () => {
  it("CustomTimeoutDoesNotBreakWorkspaceCreation", async () => {
    const originalTimeout = process.env.WEAVE_GIT_TIMEOUT_MS;
    process.env.WEAVE_GIT_TIMEOUT_MS = "30000";
    try {
      const repo = trackDir(makeGitRepo());
      const info = await createWorkspace({ sourceDirectory: repo, strategy: "clone" });
      trackDir(info.directory);
      expect(existsSync(info.directory)).toBe(true);
    } finally {
      if (originalTimeout === undefined) {
        delete process.env.WEAVE_GIT_TIMEOUT_MS;
      } else {
        process.env.WEAVE_GIT_TIMEOUT_MS = originalTimeout;
      }
    }
  });

  it("InvalidTimeoutFallsBackToDefault", async () => {
    const originalTimeout = process.env.WEAVE_GIT_TIMEOUT_MS;
    process.env.WEAVE_GIT_TIMEOUT_MS = "not-a-number";
    try {
      const repo = trackDir(makeGitRepo());
      const info = await createWorkspace({ sourceDirectory: repo, strategy: "clone" });
      trackDir(info.directory);
      // If it completes without error, the fallback to the default timeout worked
      expect(existsSync(info.directory)).toBe(true);
    } finally {
      if (originalTimeout === undefined) {
        delete process.env.WEAVE_GIT_TIMEOUT_MS;
      } else {
        process.env.WEAVE_GIT_TIMEOUT_MS = originalTimeout;
      }
    }
  });
});

describe("git error messages", () => {
  it("WorktreeOnNonGitDirGivesDescriptiveError", async () => {
    const dir = trackDir(makeTempDir());
    await expect(
      createWorkspace({ sourceDirectory: dir, strategy: "worktree" })
    ).rejects.toThrow("not a git repository");
  });
});

// ─── Profile awareness ────────────────────────────────────────────────────────

describe("workspace-manager — profile awareness", () => {
  afterEach(() => {
    delete process.env.WEAVE_PROFILE;
    delete process.env.WEAVE_WORKSPACE_ROOT;
  });

  it("UsesProfileWorkspaceRootWhenWeaveProfileIsSet", () => {
    process.env.WEAVE_PROFILE = "test-ws-profile";
    delete process.env.WEAVE_WORKSPACE_ROOT;

    const expected = resolve(homedir(), ".weave", "profiles", "test-ws-profile", "workspaces");
    expect(getProfileWorkspaceRoot()).toBe(expected);
  });

  it("WeaveWorkspaceRootOverrideTakesPrecedenceOverProfile", () => {
    process.env.WEAVE_PROFILE = "test-ws-profile";
    const override = join(tmpdir(), `ws-override-${randomUUID()}`);
    process.env.WEAVE_WORKSPACE_ROOT = override;

    expect(getProfileWorkspaceRoot()).toBe(override);
  });
});
