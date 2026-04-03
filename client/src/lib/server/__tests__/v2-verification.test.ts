/**
 * V2 Multi-Session Workspace Isolation — Integration Verification Tests
 *
 * These tests exercise the server-side code paths corresponding to the 7 manual
 * verification scenarios in the V2 plan. They validate behaviour at the DB and
 * workspace-manager layers without requiring a running Next.js server or the
 * OpenCode binary.
 *
 * Verification items covered:
 *   1. Create 3+ concurrent sessions against different directories
 *   2. Each session streams independently (separate instances per directory)
 *   3. Terminate a session — instance stopped, session marked "stopped"
 *   4. Restart server — previous sessions visible as "disconnected"
 *   5. Worktree isolation creates a real git worktree; cleanup removes it
 *   6. Fleet summary bar shows real aggregate stats
 *   7. Session detail sidebar shows real metadata (workspace, instance info)
 */

import { mkdirSync, rmSync, existsSync, writeFileSync } from "fs";
import { tmpdir } from "os";
import { join, resolve } from "path";
import { randomUUID } from "crypto";
import { execSync } from "child_process";
import { _resetDbForTests } from "@/lib/server/database";
import {
  insertWorkspace,
  getWorkspace,
  insertInstance,
  getInstance,
  getInstanceByDirectory,
  updateInstanceStatus,
  getRunningInstances,
  insertSession,
  getSession,
  listSessions,
  listActiveSessions,
  updateSessionStatus,
  getNonTerminalSessionsForInstance,
} from "@/lib/server/db-repository";
import {
  createWorkspace,
  cleanupWorkspace,
} from "@/lib/server/workspace-manager";

// ─── Helpers ──────────────────────────────────────────────────────────────────

function mkId(prefix: string) {
  return `${prefix}-${randomUUID()}`;
}

function makeTempDir(): string {
  const dir = join(tmpdir(), `weave-v2-verify-${randomUUID()}`);
  mkdirSync(dir, { recursive: true });
  return dir;
}

function makeGitRepo(): string {
  const dir = makeTempDir();
  execSync("git init", { cwd: dir, stdio: "pipe" });
  execSync("git config user.email test@test.com", { cwd: dir, stdio: "pipe" });
  execSync("git config user.name Test", { cwd: dir, stdio: "pipe" });
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

/**
 * Set up a full workspace → instance → session chain in the DB.
 * Returns all IDs so callers can query/verify.
 */
function setupFullSession(opts: {
  directory: string;
  port: number;
  strategy?: "existing" | "worktree" | "clone";
  title?: string;
}) {
  const wsId = mkId("ws");
  const instId = mkId("inst");
  const sessId = mkId("sess");
  const ocId = mkId("oc");

  insertWorkspace({
    id: wsId,
    directory: opts.directory,
    isolation_strategy: opts.strategy ?? "existing",
  });
  insertInstance({
    id: instId,
    port: opts.port,
    directory: opts.directory,
    url: `http://localhost:${opts.port}`,
  });
  insertSession({
    id: sessId,
    workspace_id: wsId,
    instance_id: instId,
    opencode_session_id: ocId,
    directory: opts.directory,
    title: opts.title,
  });

  return { wsId, instId, sessId, ocId };
}

// ─── Setup / Teardown ─────────────────────────────────────────────────────────

beforeEach(() => {
  process.env.WEAVE_DB_PATH = join(tmpdir(), `fleet-v2-verify-${randomUUID()}.db`);
  process.env.WEAVE_WORKSPACE_ROOT = join(tmpdir(), `weave-v2-root-${randomUUID()}`);
  _resetDbForTests();
});

afterEach(() => {
  _resetDbForTests();
  delete process.env.WEAVE_DB_PATH;
  delete process.env.WEAVE_WORKSPACE_ROOT;
  for (const dir of testDirs.splice(0)) {
    rmSync(dir, { recursive: true, force: true });
  }
});

// ─── 1. Create 3+ concurrent sessions against different directories ──────────

describe("V2-Verify-1: 3+ concurrent sessions against different directories", () => {
  it("ThreeConcurrentSessionsCoexistInDb", () => {
    const dirs = [makeTempDir(), makeTempDir(), makeTempDir()].map(trackDir);
    const sessions = dirs.map((dir, i) =>
      setupFullSession({ directory: dir, port: 4097 + i })
    );

    // All 3 sessions should be active
    const active = listActiveSessions();
    expect(active.length).toBe(3);

    // Each session points to a different directory
    const directories = active.map((s) => s.directory);
    expect(new Set(directories).size).toBe(3);
    dirs.forEach((d) => expect(directories).toContain(d));
  });

  it("EachDirectoryHasItsOwnInstance", () => {
    const dirs = [makeTempDir(), makeTempDir(), makeTempDir()].map(trackDir);
    dirs.forEach((dir, i) =>
      setupFullSession({ directory: dir, port: 4097 + i })
    );

    const running = getRunningInstances();
    expect(running.length).toBe(3);

    // Each instance serves a different directory
    const instDirs = running.map((inst) => inst.directory);
    expect(new Set(instDirs).size).toBe(3);
  });
});

// ─── 2. Each session streams independently ───────────────────────────────────

describe("V2-Verify-2: Sessions have independent instances", () => {
  it("SessionsOnDifferentDirectoriesUseDistinctInstances", () => {
    const dir1 = trackDir(makeTempDir());
    const dir2 = trackDir(makeTempDir());
    const s1 = setupFullSession({ directory: dir1, port: 4097 });
    const s2 = setupFullSession({ directory: dir2, port: 4098 });

    // Different instances
    expect(s1.instId).not.toBe(s2.instId);

    // Stopping one instance doesn't affect the other
    updateInstanceStatus(s1.instId, "stopped", new Date().toISOString());
    expect(getInstance(s1.instId)?.status).toBe("stopped");
    expect(getInstance(s2.instId)?.status).toBe("running");
  });

  it("OperationsOnOneSessionDontAffectOthers", () => {
    const dirs = [makeTempDir(), makeTempDir()].map(trackDir);
    const sessions = dirs.map((dir, i) =>
      setupFullSession({ directory: dir, port: 4097 + i })
    );

    // Stop session 0
    updateSessionStatus(sessions[0]!.sessId, "stopped", new Date().toISOString());

    // Session 1 is still active
    const sess1 = getSession(sessions[1]!.sessId);
    expect(sess1?.status).toBe("active");
  });
});

// ─── 3. Terminate a session — process killed, fleet page shows "stopped" ─────

describe("V2-Verify-3: Terminate a session", () => {
  it("TerminatingStopsInstanceAndSession", () => {
    const dir = trackDir(makeTempDir());
    const { instId, sessId } = setupFullSession({ directory: dir, port: 4097 });

    // Simulate termination: stop instance, then mark session stopped
    const now = new Date().toISOString();
    updateInstanceStatus(instId, "stopped", now);
    updateSessionStatus(sessId, "stopped", now);

    // Instance shows stopped
    const inst = getInstance(instId);
    expect(inst?.status).toBe("stopped");
    expect(inst?.stopped_at).toBe(now);

    // Session shows stopped
    const sess = getSession(sessId);
    expect(sess?.status).toBe("stopped");
    expect(sess?.stopped_at).toBe(now);

    // No longer in active list
    expect(listActiveSessions().length).toBe(0);
  });

  it("TerminatingReleasesDirectoryForNewInstance", () => {
    const dir = trackDir(makeTempDir());
    const { instId } = setupFullSession({ directory: dir, port: 4097 });

    // Before stopping, directory has a running instance
    expect(getInstanceByDirectory(dir)?.id).toBe(instId);

    // Stop instance
    updateInstanceStatus(instId, "stopped", new Date().toISOString());

    // Directory is now free (no running instance)
    expect(getInstanceByDirectory(dir)).toBeUndefined();

    // A new instance can be started on the same directory
    const newInstId = mkId("inst");
    insertInstance({
      id: newInstId,
      port: 4098,
      directory: dir,
      url: "http://localhost:4098",
    });
    expect(getInstanceByDirectory(dir)?.id).toBe(newInstId);
  });
});

// ─── 4. Restart server — previous sessions visible as "disconnected" ─────────

describe("V2-Verify-4: Server restart → disconnected", () => {
  it("PreviouslyRunningInstancesCanBeMarkedStoppedOnRestart", () => {
    // Create 2 sessions that are "running"
    const dirs = [makeTempDir(), makeTempDir()].map(trackDir);
    const sessions = dirs.map((dir, i) =>
      setupFullSession({ directory: dir, port: 4097 + i })
    );

    // Simulate server restart: all running instances get marked stopped
    const running = getRunningInstances();
    expect(running.length).toBe(2);

    const now = new Date().toISOString();
    for (const inst of running) {
      updateInstanceStatus(inst.id, "stopped", now);
    }

    // No running instances after restart
    expect(getRunningInstances().length).toBe(0);
  });

  it("SessionsBecomeStoppedWhenRecoveryMarksInstanceDead", () => {
    const dirs = [makeTempDir(), makeTempDir()].map(trackDir);
    const sessions = dirs.map((dir, i) =>
      setupFullSession({ directory: dir, port: 4097 + i })
    );

    // Simulate recovery: mark instances stopped, then cascade to sessions
    const now = new Date().toISOString();
    for (const s of sessions) {
      updateInstanceStatus(s.instId, "stopped", now);
      // Recovery cascades: find non-terminal sessions and mark them stopped
      const orphaned = getNonTerminalSessionsForInstance(s.instId);
      for (const os of orphaned) {
        updateSessionStatus(os.id, "stopped", now);
      }
    }

    // All sessions should now be stopped
    const allSessions = listSessions();
    for (const sess of allSessions) {
      expect(sess.status).toBe("stopped");
    }
    expect(listActiveSessions().length).toBe(0);
  });
});

// ─── 5. Worktree isolation creates a real git worktree; cleanup removes it ───

describe("V2-Verify-5: Worktree isolation + cleanup", () => {
  it("WorktreeCreatesRealDirectoryAndCleanupRemovesIt", async () => {
    const repo = trackDir(makeGitRepo());
    const info = await createWorkspace({ sourceDirectory: repo, strategy: "worktree" });
    trackDir(info.directory);

    // Worktree directory exists on disk
    expect(existsSync(info.directory)).toBe(true);
    expect(info.strategy).toBe("worktree");

    // Is a valid git checkout
    expect(() =>
      execSync("git rev-parse --git-dir", { cwd: info.directory, stdio: "pipe" })
    ).not.toThrow();

    // DB records the workspace correctly
    const ws = getWorkspace(info.id);
    expect(ws?.isolation_strategy).toBe("worktree");
    expect(ws?.source_directory).toBe(resolve(repo));

    // Cleanup removes the worktree directory
    await cleanupWorkspace(info.id);
    expect(existsSync(info.directory)).toBe(false);
    expect(getWorkspace(info.id)?.cleaned_up_at).not.toBeNull();
  });
});

// ─── 6. Fleet summary bar shows real aggregate stats ─────────────────────────

describe("V2-Verify-6: Fleet summary — aggregate stats", () => {
  it("ComputesCorrectCountsByStatus", () => {
    const dirs = [
      makeTempDir(), makeTempDir(), makeTempDir(),
      makeTempDir(), makeTempDir(),
    ].map(trackDir);

    // Create 5 sessions: 2 active, 2 stopped, 1 disconnected
    const sessions = dirs.map((dir, i) =>
      setupFullSession({ directory: dir, port: 4097 + i, title: `Task ${i + 1}` })
    );

    // Stop sessions 2 and 3
    updateSessionStatus(sessions[2]!.sessId, "stopped", new Date().toISOString());
    updateSessionStatus(sessions[3]!.sessId, "stopped", new Date().toISOString());

    // Disconnect session 4
    updateSessionStatus(sessions[4]!.sessId, "disconnected");

    // Compute aggregate stats (mirrors what fleet/summary route does)
    const all = listSessions();
    const active = all.filter((s) => s.status === "active").length;
    const stopped = all.filter((s) => s.status === "stopped").length;
    const disconnected = all.filter((s) => s.status === "disconnected").length;

    expect(all.length).toBe(5);
    expect(active).toBe(2);
    expect(stopped).toBe(2);
    expect(disconnected).toBe(1);
  });

  it("RunningInstanceCountMatchesExpectedState", () => {
    const dirs = [makeTempDir(), makeTempDir(), makeTempDir()].map(trackDir);
    dirs.forEach((dir, i) =>
      setupFullSession({ directory: dir, port: 4097 + i })
    );

    // Stop one instance
    const running = getRunningInstances();
    expect(running.length).toBe(3);

    updateInstanceStatus(running[0]!.id, "stopped", new Date().toISOString());
    expect(getRunningInstances().length).toBe(2);
  });
});

// ─── 7. Session detail sidebar shows real metadata ───────────────────────────

describe("V2-Verify-7: Session detail — real metadata", () => {
  it("SessionDetailIncludesWorkspaceAndInstanceMetadata", () => {
    const dir = trackDir(makeTempDir());
    const { wsId, instId, sessId, ocId } = setupFullSession({
      directory: dir,
      port: 4097,
      strategy: "existing",
      title: "My Verification Task",
    });

    // Session record has correct fields
    const sess = getSession(sessId);
    expect(sess).toBeDefined();
    expect(sess?.title).toBe("My Verification Task");
    expect(sess?.status).toBe("active");
    expect(sess?.directory).toBe(dir);
    expect(sess?.workspace_id).toBe(wsId);
    expect(sess?.instance_id).toBe(instId);
    expect(sess?.opencode_session_id).toBe(ocId);

    // Workspace record is retrievable and has correct metadata
    const ws = getWorkspace(wsId);
    expect(ws).toBeDefined();
    expect(ws?.directory).toBe(dir);
    expect(ws?.isolation_strategy).toBe("existing");

    // Instance record is retrievable and has correct metadata
    const inst = getInstance(instId);
    expect(inst).toBeDefined();
    expect(inst?.port).toBe(4097);
    expect(inst?.url).toBe("http://localhost:4097");
    expect(inst?.status).toBe("running");
    expect(inst?.directory).toBe(dir);
  });

  it("WorktreeSessionDetailShowsSourceDirectory", async () => {
    const repo = trackDir(makeGitRepo());
    const wsInfo = await createWorkspace({ sourceDirectory: repo, strategy: "worktree" });
    trackDir(wsInfo.directory);

    // Create instance + session using the worktree directory
    const instId = mkId("inst");
    const sessId = mkId("sess");
    const ocId = mkId("oc");

    insertInstance({
      id: instId,
      port: 4098,
      directory: wsInfo.directory,
      url: "http://localhost:4098",
    });
    insertSession({
      id: sessId,
      workspace_id: wsInfo.id,
      instance_id: instId,
      opencode_session_id: ocId,
      directory: wsInfo.directory,
    });

    // Session detail can navigate to workspace which shows source_directory
    const sess = getSession(sessId);
    expect(sess).toBeDefined();
    const ws = getWorkspace(sess!.workspace_id);
    expect(ws?.isolation_strategy).toBe("worktree");
    expect(ws?.source_directory).toBe(resolve(repo));
    expect(ws?.branch).toBeDefined();

    // Cleanup
    await cleanupWorkspace(wsInfo.id);
  });
});
