import { tmpdir, homedir } from "os";
import { join } from "path";
import { randomUUID } from "crypto";
import { getDb, _resetDbForTests } from "@/lib/server/database";
import { getProfileDbPath } from "@/lib/server/profile";

describe("database module", () => {
  beforeEach(() => {
    // Use a unique temp file per test to avoid cross-test contamination
    process.env.WEAVE_DB_PATH = join(tmpdir(), `fleet-test-${randomUUID()}.db`);
    _resetDbForTests();
  });

  afterEach(() => {
    _resetDbForTests();
    delete process.env.WEAVE_DB_PATH;
    delete process.env.WEAVE_PROFILE;
  });

  it("CreatesDbFileAndReturnsInstance", () => {
    const db = getDb();
    expect(db).toBeDefined();
    // Should be open
    expect(db.open).toBe(true);
  });

  it("ReturnsSameSingletonOnMultipleCalls", () => {
    const db1 = getDb();
    const db2 = getDb();
    expect(db1).toBe(db2);
  });

  it("CreatesWorkspacesTable", () => {
    const db = getDb();
    const result = db
      .prepare("SELECT name FROM sqlite_master WHERE type='table' AND name='workspaces'")
      .get() as { name: string } | undefined;
    expect(result?.name).toBe("workspaces");
  });

  it("CreatesInstancesTable", () => {
    const db = getDb();
    const result = db
      .prepare("SELECT name FROM sqlite_master WHERE type='table' AND name='instances'")
      .get() as { name: string } | undefined;
    expect(result?.name).toBe("instances");
  });

  it("CreatesSessionsTable", () => {
    const db = getDb();
    const result = db
      .prepare("SELECT name FROM sqlite_master WHERE type='table' AND name='sessions'")
      .get() as { name: string } | undefined;
    expect(result?.name).toBe("sessions");
  });

  it("CreatesWorkspaceRootsTable", () => {
    const db = getDb();
    const result = db
      .prepare("SELECT name FROM sqlite_master WHERE type='table' AND name='workspace_roots'")
      .get() as { name: string } | undefined;
    expect(result?.name).toBe("workspace_roots");
  });

  it("EnablesWalMode", () => {
    const db = getDb();
    const result = db.pragma("journal_mode") as { journal_mode: string }[];
    expect(result[0]?.journal_mode).toBe("wal");
  });

  it("CanInsertAndReadRow", () => {
    const db = getDb();
    db.prepare(
      "INSERT INTO workspaces (id, directory, isolation_strategy) VALUES (?, ?, ?)"
    ).run("test-id", "/tmp/test", "existing");

    const row = db
      .prepare("SELECT * FROM workspaces WHERE id = ?")
      .get("test-id") as { id: string; directory: string } | undefined;
    expect(row?.id).toBe("test-id");
    expect(row?.directory).toBe("/tmp/test");
  });

  it("ResetDbForTestsClosesAndDeletesDb", () => {
    const db = getDb();
    expect(db.open).toBe(true);
    _resetDbForTests();
    // After reset, getting db again creates a fresh instance
    const db2 = getDb();
    expect(db2).not.toBe(db);
  });
});

describe("database module — profile awareness", () => {
  afterEach(() => {
    _resetDbForTests();
    delete process.env.WEAVE_DB_PATH;
    delete process.env.WEAVE_PROFILE;
  });

  it("UsesProfileDirWhenWeaveProfileIsSet", () => {
    process.env.WEAVE_PROFILE = "test-db-profile";
    delete process.env.WEAVE_DB_PATH;
    _resetDbForTests();

    const expectedPath = join(homedir(), ".weave", "profiles", "test-db-profile", "fleet.db");
    expect(getProfileDbPath()).toBe(expectedPath);
  });

  it("WeaveDbPathOverrideTakesPrecedenceOverProfile", () => {
    process.env.WEAVE_PROFILE = "test-db-profile";
    const override = join(tmpdir(), `override-test-${randomUUID()}.db`);
    process.env.WEAVE_DB_PATH = override;
    _resetDbForTests();

    expect(getProfileDbPath()).toBe(override);
  });
});
