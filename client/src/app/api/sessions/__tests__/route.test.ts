import { vi } from "vitest";
import { NextRequest } from "next/server";

// ─── Mocks ───────────────────────────────────────────────────────────────────

vi.mock("@/lib/server/process-manager", () => ({
  spawnInstance: vi.fn(),
  listInstances: vi.fn(() => []),
  validateDirectory: vi.fn((dir: string) => dir),
  _recoveryComplete: Promise.resolve(),
  destroyInstance: vi.fn(),
}));

vi.mock("@/lib/server/workspace-manager", () => ({
  createWorkspace: vi.fn(),
  cleanupWorkspace: vi.fn(),
}));

vi.mock("@/lib/server/db-repository", () => ({
  insertSession: vi.fn(),
  listSessions: vi.fn(() => []),
  countSessions: vi.fn(() => 0),
  getWorkspace: vi.fn(),
  getInstance: vi.fn(),
  getSession: vi.fn(),
  getSessionByHarnessId: vi.fn(),
  updateSessionStatus: vi.fn(),
  updateSessionTitle: vi.fn(),
  getSessionsForInstance: vi.fn(() => []),
  getSessionIdsWithActiveChildren: vi.fn(() => new Set()),
  insertSessionCallback: vi.fn(),
}));

vi.mock("@/lib/server/opencode-client", () => ({
  getClientForInstance: vi.fn(),
}));

vi.mock("@/lib/server/context-formatter", () => ({
  formatContextAsPrompt: vi.fn((ctx: unknown) => `Formatted: ${JSON.stringify(ctx)}`),
}));

vi.mock("crypto", async (importOriginal) => {
  const original = await importOriginal<typeof import("crypto")>();
  return {
    ...original,
    randomUUID: vi.fn(() => "test-uuid-1234"),
  };
});

// ─── Imports (after mocks) ────────────────────────────────────────────────────

import { POST, GET } from "@/app/api/sessions/route";
import { DELETE, PATCH } from "@/app/api/sessions/[id]/route";
import * as processManager from "@/lib/server/process-manager";
import * as workspaceManager from "@/lib/server/workspace-manager";
import * as dbRepository from "@/lib/server/db-repository";
import * as contextFormatter from "@/lib/server/context-formatter";

// ─── Typed mock helpers ───────────────────────────────────────────────────────

const mockSpawnInstance = vi.mocked(processManager.spawnInstance);
const mockListInstances = vi.mocked(processManager.listInstances);
const mockValidateDirectory = vi.mocked(processManager.validateDirectory);
const mockDestroyInstance = vi.mocked(processManager.destroyInstance);
const mockCreateWorkspace = vi.mocked(workspaceManager.createWorkspace);
const mockCleanupWorkspace = vi.mocked(workspaceManager.cleanupWorkspace);
const mockInsertSession = vi.mocked(dbRepository.insertSession);
const mockListSessions = vi.mocked(dbRepository.listSessions);
const mockCountSessions = vi.mocked(dbRepository.countSessions);
const mockGetWorkspace = vi.mocked(dbRepository.getWorkspace);
const mockGetInstance = vi.mocked(dbRepository.getInstance);
const mockGetSession = vi.mocked(dbRepository.getSession);
const mockGetSessionByOpencodeId = vi.mocked(dbRepository.getSessionByHarnessId);
const mockUpdateSessionStatus = vi.mocked(dbRepository.updateSessionStatus);
const mockGetSessionsForInstance = vi.mocked(dbRepository.getSessionsForInstance);
const mockUpdateSessionTitle = vi.mocked(dbRepository.updateSessionTitle);
const mockGetSessionIdsWithActiveChildren = vi.mocked(dbRepository.getSessionIdsWithActiveChildren);
const mockFormatContextAsPrompt = vi.mocked(contextFormatter.formatContextAsPrompt);

// ─── Shared fixtures ──────────────────────────────────────────────────────────

function makeSdkSession(overrides: Record<string, unknown> = {}) {
  return {
    id: "oc-session-abc",
    title: "Test Session",
    directory: "/home/user/project",
    projectID: "proj-1",
    version: "1",
    time: { created: 1700000000, updated: 1700000001 },
    ...overrides,
  };
}

function makeWorkspaceInfo(overrides: Record<string, unknown> = {}) {
  return {
    id: "ws-123",
    directory: "/home/user/project",
    strategy: "existing" as const,
    ...overrides,
  };
}

function makeManagedInstance(overrides: Record<string, unknown> = {}) {
  return {
    id: "inst-abc",
    port: 4097,
    url: "http://localhost:4097",
    directory: "/home/user/project",
    status: "running" as const,
    createdAt: new Date(),
    recovered: false,
    close: vi.fn(),
    client: {
      session: {
        create: vi.fn(),
        get: vi.fn(),
        list: vi.fn(),
        messages: vi.fn(),
        status: vi.fn(),
        promptAsync: vi.fn().mockResolvedValue(undefined),
      },
    },
    ...overrides,
  };
}

function makeDbSession(overrides: Record<string, unknown> = {}) {
  return {
    id: "db-sess-1",
    workspace_id: "ws-123",
    instance_id: "inst-abc",
    opencode_session_id: "oc-session-abc",
    title: "Test Session",
    directory: "/home/user/project",
    status: "active" as const,
    created_at: new Date("2024-01-01T00:00:00Z").toISOString(),
    stopped_at: null,
    ...overrides,
  };
}

function makeDbWorkspace(overrides: Record<string, unknown> = {}) {
  return {
    id: "ws-123",
    directory: "/home/user/project",
    isolation_strategy: "existing",
    source_directory: "/home/user/project",
    branch: null,
    display_name: null,
    cleaned_up_at: null,
    created_at: new Date("2024-01-01T00:00:00Z").toISOString(),
    ...overrides,
  };
}

// ─── POST /api/sessions ───────────────────────────────────────────────────────

describe("POST /api/sessions", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    mockValidateDirectory.mockImplementation((dir: string) => dir);
  });

  it("Returns400ForInvalidJsonBody", async () => {
    const req = new NextRequest("http://localhost/api/sessions", {
      method: "POST",
      body: "not-valid-json{{{",
      headers: { "Content-Type": "application/json" },
    });

    const res = await POST(req);
    const body = await res.json();

    expect(res.status).toBe(400);
    expect(body.error).toMatch(/invalid json/i);
  });

  it("Returns400WhenDirectoryIsMissing", async () => {
    const req = new NextRequest("http://localhost/api/sessions", {
      method: "POST",
      body: JSON.stringify({ title: "Hello" }),
      headers: { "Content-Type": "application/json" },
    });

    const res = await POST(req);
    const body = await res.json();

    expect(res.status).toBe(400);
    expect(body.error).toMatch(/directory is required/i);
  });

  it("Returns400WhenDirectoryIsEmptyString", async () => {
    const req = new NextRequest("http://localhost/api/sessions", {
      method: "POST",
      body: JSON.stringify({ directory: "" }),
      headers: { "Content-Type": "application/json" },
    });

    const res = await POST(req);
    const body = await res.json();

    expect(res.status).toBe(400);
    expect(body.error).toMatch(/directory is required/i);
  });

  it("Returns400WhenValidateDirectoryThrows", async () => {
    mockValidateDirectory.mockImplementation(() => {
      throw new Error("Directory is outside the allowed workspace roots");
    });

    const req = new NextRequest("http://localhost/api/sessions", {
      method: "POST",
      body: JSON.stringify({ directory: "/outside/root" }),
      headers: { "Content-Type": "application/json" },
    });

    const res = await POST(req);
    const body = await res.json();

    expect(res.status).toBe(400);
    expect(body.error).toBe("Directory is outside the allowed workspace roots");
  });

  it("Returns200WithCreateSessionResponseOnSuccess", async () => {
    const workspace = makeWorkspaceInfo();
    const sdkSession = makeSdkSession();
    const instance = makeManagedInstance();
    instance.client.session.create = vi.fn().mockResolvedValue({ data: sdkSession });

    mockCreateWorkspace.mockResolvedValue(workspace);
    mockSpawnInstance.mockResolvedValue(instance as never);

    const req = new NextRequest("http://localhost/api/sessions", {
      method: "POST",
      body: JSON.stringify({ directory: "/home/user/project", title: "My Task" }),
      headers: { "Content-Type": "application/json" },
    });

    const res = await POST(req);
    const body = await res.json();

    expect(res.status).toBe(200);
    expect(body.instanceId).toBe("inst-abc");
    expect(body.workspaceId).toBe("ws-123");
    expect(body.session).toMatchObject({ id: "oc-session-abc", title: "Test Session" });
  });

  it("Returns500WhenSdkSessionCreateReturnsNoData", async () => {
    const workspace = makeWorkspaceInfo();
    const instance = makeManagedInstance();
    instance.client.session.create = vi.fn().mockResolvedValue({ data: null });

    mockCreateWorkspace.mockResolvedValue(workspace);
    mockSpawnInstance.mockResolvedValue(instance as never);

    const req = new NextRequest("http://localhost/api/sessions", {
      method: "POST",
      body: JSON.stringify({ directory: "/home/user/project" }),
      headers: { "Content-Type": "application/json" },
    });

    const res = await POST(req);
    const body = await res.json();

    expect(res.status).toBe(500);
    expect(body.error).toMatch(/no data/i);
  });

  it("Returns200EvenWhenDbInsertFails", async () => {
    const workspace = makeWorkspaceInfo();
    const sdkSession = makeSdkSession();
    const instance = makeManagedInstance();
    instance.client.session.create = vi.fn().mockResolvedValue({ data: sdkSession });

    mockCreateWorkspace.mockResolvedValue(workspace);
    mockSpawnInstance.mockResolvedValue(instance as never);
    mockInsertSession.mockImplementation(() => {
      throw new Error("DB write failed");
    });

    const req = new NextRequest("http://localhost/api/sessions", {
      method: "POST",
      body: JSON.stringify({ directory: "/home/user/project" }),
      headers: { "Content-Type": "application/json" },
    });

    const res = await POST(req);
    const body = await res.json();

    expect(res.status).toBe(200);
    expect(body.session).toBeDefined();
  });

  it("Returns500WhenCreateWorkspaceThrows", async () => {
    mockCreateWorkspace.mockRejectedValue(new Error("workspace creation failed"));

    const req = new NextRequest("http://localhost/api/sessions", {
      method: "POST",
      body: JSON.stringify({ directory: "/home/user/project" }),
      headers: { "Content-Type": "application/json" },
    });

    const res = await POST(req);
    const body = await res.json();

    expect(res.status).toBe(500);
    expect(body.error).toMatch(/failed to create session/i);
  });

  it("UsesDefaultIsolationStrategyExistingWhenNotSpecified", async () => {
    const workspace = makeWorkspaceInfo();
    const sdkSession = makeSdkSession();
    const instance = makeManagedInstance();
    instance.client.session.create = vi.fn().mockResolvedValue({ data: sdkSession });

    mockCreateWorkspace.mockResolvedValue(workspace);
    mockSpawnInstance.mockResolvedValue(instance as never);

    const req = new NextRequest("http://localhost/api/sessions", {
      method: "POST",
      body: JSON.stringify({ directory: "/home/user/project" }),
      headers: { "Content-Type": "application/json" },
    });

    await POST(req);

    expect(mockCreateWorkspace).toHaveBeenCalledWith(
      expect.objectContaining({ strategy: "existing" })
    );
  });

  it("PassesTitleThroughToSessionCreate", async () => {
    const workspace = makeWorkspaceInfo();
    const sdkSession = makeSdkSession();
    const instance = makeManagedInstance();
    const createMock = vi.fn().mockResolvedValue({ data: sdkSession });
    instance.client.session.create = createMock;

    mockCreateWorkspace.mockResolvedValue(workspace);
    mockSpawnInstance.mockResolvedValue(instance as never);

    const req = new NextRequest("http://localhost/api/sessions", {
      method: "POST",
      body: JSON.stringify({ directory: "/home/user/project", title: "Custom Title" }),
      headers: { "Content-Type": "application/json" },
    });

    await POST(req);

    expect(createMock).toHaveBeenCalledWith(
      expect.objectContaining({ title: "Custom Title" })
    );
  });

  it("CallsFormatContextAsPromptAndSendsPromptWhenContextProvided", async () => {
    const workspace = makeWorkspaceInfo();
    const sdkSession = makeSdkSession();
    const instance = makeManagedInstance();
    instance.client.session.create = vi.fn().mockResolvedValue({ data: sdkSession });
    const promptAsyncMock = vi.fn().mockResolvedValue(undefined);
    instance.client.session.promptAsync = promptAsyncMock;

    mockCreateWorkspace.mockResolvedValue(workspace);
    mockSpawnInstance.mockResolvedValue(instance as never);
    mockFormatContextAsPrompt.mockReturnValue("Formatted context prompt");

    const context = {
      type: "github-issue" as const,
      title: "Fix login bug",
      url: "https://github.com/acme/app/issues/42",
      body: "Login fails when email has uppercase letters",
      number: 42,
      labels: ["bug"],
      author: "user1",
    };

    const req = new NextRequest("http://localhost/api/sessions", {
      method: "POST",
      body: JSON.stringify({ directory: "/home/user/project", context }),
      headers: { "Content-Type": "application/json" },
    });

    const res = await POST(req);
    expect(res.status).toBe(200);

    expect(mockFormatContextAsPrompt).toHaveBeenCalledWith(context);
    expect(promptAsyncMock).toHaveBeenCalledWith({
      sessionID: "oc-session-abc",
      parts: [{ type: "text", text: "Formatted context prompt" }],
    });
  });

  it("UsesTitleFromContextWhenNoExplicitTitleProvided", async () => {
    const workspace = makeWorkspaceInfo();
    const sdkSession = makeSdkSession();
    const instance = makeManagedInstance();
    const createMock = vi.fn().mockResolvedValue({ data: sdkSession });
    instance.client.session.create = createMock;

    mockCreateWorkspace.mockResolvedValue(workspace);
    mockSpawnInstance.mockResolvedValue(instance as never);

    const context = {
      type: "github-pr" as const,
      title: "Add dark mode",
      url: "https://github.com/acme/app/pulls/7",
      number: 7,
      body: "",
      author: "dev1",
    };

    const req = new NextRequest("http://localhost/api/sessions", {
      method: "POST",
      body: JSON.stringify({ directory: "/home/user/project", context }),
      headers: { "Content-Type": "application/json" },
    });

    await POST(req);

    expect(createMock).toHaveBeenCalledWith(
      expect.objectContaining({ title: "Add dark mode" })
    );
  });

  it("SendsInitialPromptDirectlyWhenInitialPromptProvided", async () => {
    const workspace = makeWorkspaceInfo();
    const sdkSession = makeSdkSession();
    const instance = makeManagedInstance();
    instance.client.session.create = vi.fn().mockResolvedValue({ data: sdkSession });
    const promptAsyncMock = vi.fn().mockResolvedValue(undefined);
    instance.client.session.promptAsync = promptAsyncMock;

    mockCreateWorkspace.mockResolvedValue(workspace);
    mockSpawnInstance.mockResolvedValue(instance as never);

    const req = new NextRequest("http://localhost/api/sessions", {
      method: "POST",
      body: JSON.stringify({ directory: "/home/user/project", initialPrompt: "Do the thing" }),
      headers: { "Content-Type": "application/json" },
    });

    const res = await POST(req);
    expect(res.status).toBe(200);

    expect(mockFormatContextAsPrompt).not.toHaveBeenCalled();
    expect(promptAsyncMock).toHaveBeenCalledWith({
      sessionID: "oc-session-abc",
      parts: [{ type: "text", text: "Do the thing" }],
    });
  });

  it("DoesNotCallPromptAsyncWhenNoContextOrInitialPrompt", async () => {
    const workspace = makeWorkspaceInfo();
    const sdkSession = makeSdkSession();
    const instance = makeManagedInstance();
    instance.client.session.create = vi.fn().mockResolvedValue({ data: sdkSession });
    const promptAsyncMock = vi.fn().mockResolvedValue(undefined);
    instance.client.session.promptAsync = promptAsyncMock;

    mockCreateWorkspace.mockResolvedValue(workspace);
    mockSpawnInstance.mockResolvedValue(instance as never);

    const req = new NextRequest("http://localhost/api/sessions", {
      method: "POST",
      body: JSON.stringify({ directory: "/home/user/project", title: "Plain session" }),
      headers: { "Content-Type": "application/json" },
    });

    const res = await POST(req);
    expect(res.status).toBe(200);

    expect(mockFormatContextAsPrompt).not.toHaveBeenCalled();
    expect(promptAsyncMock).not.toHaveBeenCalled();
  });
});

// ─── GET /api/sessions ────────────────────────────────────────────────────────

describe("GET /api/sessions", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    mockListInstances.mockReturnValue([]);
    mockListSessions.mockReturnValue([]);
    mockGetWorkspace.mockReturnValue(undefined as never);
    mockGetInstance.mockReturnValue(undefined as never);
    mockGetSessionIdsWithActiveChildren.mockReturnValue(new Set());
  });

  it("ReturnsEmptyArrayWhenNoSessionsExist", async () => {
    mockListSessions.mockReturnValue([]);
    mockListInstances.mockReturnValue([]);

    const res = await GET(new NextRequest("http://localhost/api/sessions"));
    const body = await res.json();

    expect(res.status).toBe(200);
    expect(body).toEqual([]);
  });

  it("ReturnsActiveSessionsWhenInstanceIsLiveAndRunning", async () => {
    const sdkSession = makeSdkSession();
    const dbSession = makeDbSession();
    const instance = makeManagedInstance();
    instance.client.session.get = vi.fn().mockResolvedValue({ data: sdkSession });

    mockListSessions.mockReturnValue([dbSession] as never);
    mockListInstances.mockReturnValue([instance] as never);
    mockGetWorkspace.mockReturnValue(makeDbWorkspace() as never);

    const res = await GET(new NextRequest("http://localhost/api/sessions"));
    const body = await res.json();

    expect(res.status).toBe(200);
    expect(body).toHaveLength(1);
    expect(body[0].sessionStatus).toBe("active");
    expect(body[0].instanceStatus).toBe("running");
    expect(body[0].instanceId).toBe("inst-abc");
    expect(body[0].session.id).toBe("oc-session-abc");
  });

  it("ReturnsDisconnectedStatusWhenInstanceNotInLiveMapButDbSaysRunning", async () => {
    const dbSession = makeDbSession({ status: "active" });
    const dbInstance = { id: "inst-abc", status: "running" };

    mockListSessions.mockReturnValue([dbSession] as never);
    mockListInstances.mockReturnValue([]);
    mockGetInstance.mockReturnValue(dbInstance as never);
    mockGetWorkspace.mockReturnValue(makeDbWorkspace() as never);

    const res = await GET(new NextRequest("http://localhost/api/sessions"));
    const body = await res.json();

    expect(res.status).toBe(200);
    expect(body).toHaveLength(1);
    expect(body[0].sessionStatus).toBe("disconnected");
    expect(body[0].lifecycleStatus).toBe("disconnected");
    expect(body[0].instanceStatus).toBe("dead");
  });

  it("ReturnsStoppedStatusWhenDbSessionIsStoppedAndInstanceIsDead", async () => {
    const dbSession = makeDbSession({ status: "stopped" });

    mockListSessions.mockReturnValue([dbSession] as never);
    mockListInstances.mockReturnValue([]);
    mockGetInstance.mockReturnValue(undefined as never);
    mockGetWorkspace.mockReturnValue(makeDbWorkspace() as never);

    const res = await GET(new NextRequest("http://localhost/api/sessions"));
    const body = await res.json();

    expect(res.status).toBe(200);
    expect(body).toHaveLength(1);
    expect(body[0].sessionStatus).toBe("stopped");
    expect(body[0].instanceStatus).toBe("dead");
  });

  it("FallsBackToLiveOnlyListingWhenDbIsUnavailable", async () => {
    const sdkSession = makeSdkSession();
    const instance = makeManagedInstance();
    instance.client.session.list = vi.fn().mockResolvedValue({ data: [sdkSession] });

    mockListSessions.mockImplementation(() => {
      throw new Error("DB unavailable");
    });
    mockListInstances.mockReturnValue([instance] as never);

    const res = await GET(new NextRequest("http://localhost/api/sessions"));
    const body = await res.json();

    expect(res.status).toBe(200);
    expect(body).toHaveLength(1);
    expect(body[0].instanceId).toBe("inst-abc");
    expect(body[0].session.id).toBe("oc-session-abc");
    expect(body[0].sessionStatus).toBe("active");
  });

  it("IncludesWorkspaceMetadata", async () => {
    const sdkSession = makeSdkSession();
    const dbSession = makeDbSession();
    const instance = makeManagedInstance();
    instance.client.session.get = vi.fn().mockResolvedValue({ data: sdkSession });

    const dbWorkspace = makeDbWorkspace({
      directory: "/home/user/project",
      display_name: "My Project",
      isolation_strategy: "worktree",
    });

    mockListSessions.mockReturnValue([dbSession] as never);
    mockListInstances.mockReturnValue([instance] as never);
    mockGetWorkspace.mockReturnValue(dbWorkspace as never);

    const res = await GET(new NextRequest("http://localhost/api/sessions"));
    const body = await res.json();

    expect(res.status).toBe(200);
    expect(body[0].workspaceDirectory).toBe("/home/user/project");
    expect(body[0].workspaceDisplayName).toBe("My Project");
    expect(body[0].isolationStrategy).toBe("worktree");
  });

  it("ReturnsIdleStatusWhenDbSessionIsIdleAndInstanceIsRunning", async () => {
    const sdkSession = makeSdkSession();
    const dbSession = makeDbSession({ status: "idle" });
    const instance = makeManagedInstance();
    instance.client.session.get = vi.fn().mockResolvedValue({ data: sdkSession });

    mockListSessions.mockReturnValue([dbSession] as never);
    mockListInstances.mockReturnValue([instance] as never);
    mockGetWorkspace.mockReturnValue(makeDbWorkspace() as never);

    const res = await GET(new NextRequest("http://localhost/api/sessions"));
    const body = await res.json();

    expect(res.status).toBe(200);
    expect(body).toHaveLength(1);
    expect(body[0].sessionStatus).toBe("idle");
    expect(body[0].instanceStatus).toBe("running");
  });

  it("ReturnsCompletedStatusWhenDbSessionIsCompletedAndInstanceIsDead", async () => {
    const dbSession = makeDbSession({ status: "completed" });

    mockListSessions.mockReturnValue([dbSession] as never);
    mockListInstances.mockReturnValue([]);
    mockGetInstance.mockReturnValue(undefined as never);
    mockGetWorkspace.mockReturnValue(makeDbWorkspace() as never);

    const res = await GET(new NextRequest("http://localhost/api/sessions"));
    const body = await res.json();

    expect(res.status).toBe(200);
    expect(body).toHaveLength(1);
    expect(body[0].sessionStatus).toBe("completed");
    expect(body[0].instanceStatus).toBe("dead");
  });

  it("ReturnsErrorLifecycleStatusWhenDbSessionIsInErrorState", async () => {
    const dbSession = makeDbSession({ status: "error" });
    mockListSessions.mockReturnValue([dbSession] as never);
    mockListInstances.mockReturnValue([]);
    mockGetInstance.mockReturnValue(undefined as never);
    mockGetWorkspace.mockReturnValue(makeDbWorkspace() as never);

    const res = await GET(new NextRequest("http://localhost/api/sessions"));
    const body = await res.json();

    expect(res.status).toBe(200);
    expect(body).toHaveLength(1);
    expect(body[0].sessionStatus).toBe("error");
    expect(body[0].lifecycleStatus).toBe("error");
  });

  it("SynthesizesStubSessionForDisconnectedSessionsWhenSdkFetchFails", async () => {
    const dbSession = makeDbSession({
      status: "stopped",
      opencode_session_id: "oc-session-abc",
      title: "Stub Session",
      directory: "/home/user/project",
      created_at: "2024-01-01T00:00:00Z",
      stopped_at: "2024-01-02T00:00:00Z",
    });

    mockListSessions.mockReturnValue([dbSession] as never);
    mockListInstances.mockReturnValue([]);
    mockGetInstance.mockReturnValue(undefined as never);
    mockGetWorkspace.mockReturnValue(makeDbWorkspace() as never);

    const res = await GET(new NextRequest("http://localhost/api/sessions"));
    const body = await res.json();

    expect(res.status).toBe(200);
    expect(body).toHaveLength(1);
    expect(body[0].session.id).toBe("oc-session-abc");
    expect(body[0].session.title).toBe("Stub Session");
    expect(body[0].session.directory).toBe("/home/user/project");
  });

  // ─── Parent status override tests ───────────────────────────────────────────

  it("OverridesIdleParentToBusyWhenChildIsActive", async () => {
    const sdkSession = makeSdkSession();
    const dbSession = makeDbSession({ id: "db-parent-1", status: "idle" });
    const instance = makeManagedInstance();
    instance.client.session.get = vi.fn().mockResolvedValue({ data: sdkSession });

    mockListSessions.mockReturnValue([dbSession] as never);
    mockListInstances.mockReturnValue([instance] as never);
    mockGetWorkspace.mockReturnValue(makeDbWorkspace() as never);
    mockGetSessionIdsWithActiveChildren.mockReturnValue(new Set(["db-parent-1"]));

    const res = await GET(new NextRequest("http://localhost/api/sessions"));
    const body = await res.json();

    expect(res.status).toBe(200);
    expect(body).toHaveLength(1);
    expect(body[0].sessionStatus).toBe("active");
    expect(body[0].activityStatus).toBe("busy");
  });

  it("DoesNotOverrideActiveParent", async () => {
    const sdkSession = makeSdkSession();
    const dbSession = makeDbSession({ id: "db-parent-2", status: "active" });
    const instance = makeManagedInstance();
    instance.client.session.get = vi.fn().mockResolvedValue({ data: sdkSession });

    mockListSessions.mockReturnValue([dbSession] as never);
    mockListInstances.mockReturnValue([instance] as never);
    mockGetWorkspace.mockReturnValue(makeDbWorkspace() as never);
    mockGetSessionIdsWithActiveChildren.mockReturnValue(new Set(["db-parent-2"]));

    const res = await GET(new NextRequest("http://localhost/api/sessions"));
    const body = await res.json();

    expect(res.status).toBe(200);
    expect(body).toHaveLength(1);
    expect(body[0].sessionStatus).toBe("active");
    expect(body[0].activityStatus).toBe("busy");
  });

  it("DoesNotOverrideTerminalParent", async () => {
    const dbSession = makeDbSession({ id: "db-parent-3", status: "stopped" });
    const instance = makeManagedInstance();

    mockListSessions.mockReturnValue([dbSession] as never);
    mockListInstances.mockReturnValue([instance] as never);
    mockGetWorkspace.mockReturnValue(makeDbWorkspace() as never);
    mockGetSessionIdsWithActiveChildren.mockReturnValue(new Set(["db-parent-3"]));

    const res = await GET(new NextRequest("http://localhost/api/sessions"));
    const body = await res.json();

    expect(res.status).toBe(200);
    expect(body).toHaveLength(1);
    // Terminal status is checked first — override never applies
    expect(body[0].sessionStatus).toBe("stopped");
  });

  it("IdleParentStaysIdleWhenNoActiveChildren", async () => {
    const sdkSession = makeSdkSession();
    const dbSession = makeDbSession({ id: "db-parent-4", status: "idle" });
    const instance = makeManagedInstance();
    instance.client.session.get = vi.fn().mockResolvedValue({ data: sdkSession });

    mockListSessions.mockReturnValue([dbSession] as never);
    mockListInstances.mockReturnValue([instance] as never);
    mockGetWorkspace.mockReturnValue(makeDbWorkspace() as never);
    mockGetSessionIdsWithActiveChildren.mockReturnValue(new Set());

    const res = await GET(new NextRequest("http://localhost/api/sessions"));
    const body = await res.json();

    expect(res.status).toBe(200);
    expect(body).toHaveLength(1);
    expect(body[0].sessionStatus).toBe("idle");
    expect(body[0].activityStatus).toBe("idle");
  });

  it("HandlesActiveChildrenDbQueryFailureGracefully", async () => {
    const sdkSession = makeSdkSession();
    const dbSession = makeDbSession({ id: "db-parent-5", status: "idle" });
    const instance = makeManagedInstance();
    instance.client.session.get = vi.fn().mockResolvedValue({ data: sdkSession });

    mockListSessions.mockReturnValue([dbSession] as never);
    mockListInstances.mockReturnValue([instance] as never);
    mockGetWorkspace.mockReturnValue(makeDbWorkspace() as never);
    mockGetSessionIdsWithActiveChildren.mockImplementation(() => {
      throw new Error("DB query failed");
    });

    const res = await GET(new NextRequest("http://localhost/api/sessions"));
    const body = await res.json();

    // Should still return 200 — fallback to empty set, no override
    expect(res.status).toBe(200);
    expect(body).toHaveLength(1);
    expect(body[0].sessionStatus).toBe("idle");
    expect(body[0].activityStatus).toBe("idle");
  });

  it("PassesPaginationParamsToListSessions", async () => {
    mockListSessions.mockReturnValue([]);
    mockListInstances.mockReturnValue([]);
    mockCountSessions.mockReturnValue(0);

    const res = await GET(new NextRequest("http://localhost/api/sessions?limit=50&offset=10"));
    expect(res.status).toBe(200);
    expect(mockListSessions).toHaveBeenCalledWith({ limit: 50, offset: 10, statuses: undefined });
  });

  it("PassesStatusFilterToListSessions", async () => {
    mockListSessions.mockReturnValue([]);
    mockListInstances.mockReturnValue([]);
    mockCountSessions.mockReturnValue(0);

    const res = await GET(new NextRequest("http://localhost/api/sessions?status=active,idle"));
    expect(res.status).toBe(200);
    expect(mockListSessions).toHaveBeenCalledWith({ limit: 100, offset: 0, statuses: ["active", "idle"] });
  });

  it("IncludesPaginationHeaders", async () => {
    mockListSessions.mockReturnValue([]);
    mockListInstances.mockReturnValue([]);
    mockCountSessions.mockReturnValue(42);

    const res = await GET(new NextRequest("http://localhost/api/sessions?limit=10&offset=5"));
    expect(res.headers.get("X-Total-Count")).toBe("42");
    expect(res.headers.get("X-Limit")).toBe("10");
    expect(res.headers.get("X-Offset")).toBe("5");
  });

  it("DefaultsTo100LimitWhenNoParamsGiven", async () => {
    mockListSessions.mockReturnValue([]);
    mockListInstances.mockReturnValue([]);
    mockCountSessions.mockReturnValue(0);

    const res = await GET(new NextRequest("http://localhost/api/sessions"));
    expect(res.status).toBe(200);
    expect(mockListSessions).toHaveBeenCalledWith({ limit: 100, offset: 0, statuses: undefined });
  });

  // ─── SDK timeout tests ──────────────────────────────────────────────────────

  it("UsesStubSessionWhenSessionGetTimesOut", async () => {
    const dbSession = makeDbSession({ status: "active" });
    const instance = makeManagedInstance();
    // session.get never resolves
    instance.client.session.get = vi.fn().mockReturnValue(new Promise(() => {}));

    mockListSessions.mockReturnValue([dbSession] as never);
    mockListInstances.mockReturnValue([instance] as never);
    mockGetWorkspace.mockReturnValue(makeDbWorkspace() as never);

    const orig = process.env.WEAVE_SDK_CALL_TIMEOUT_MS;
    process.env.WEAVE_SDK_CALL_TIMEOUT_MS = "50";
    try {
      const res = await GET(new NextRequest("http://localhost/api/sessions"));
      const body = await res.json();

      expect(res.status).toBe(200);
      expect(body).toHaveLength(1);
      // Falls back to stub — id still comes from DB
      expect(body[0].session.id).toBe("oc-session-abc");
      expect(body[0].session.title).toBe("Test Session");
    } finally {
      if (orig !== undefined) {
        process.env.WEAVE_SDK_CALL_TIMEOUT_MS = orig;
      } else {
        delete process.env.WEAVE_SDK_CALL_TIMEOUT_MS;
      }
    }
  });

  it("ContinuesWithoutStatusDataWhenSessionStatusTimesOut", async () => {
    const sdkSession = makeSdkSession();
    const dbSession = makeDbSession({ status: "idle" });
    const instance = makeManagedInstance();
    // session.status never resolves
    instance.client.session.status = vi.fn().mockReturnValue(new Promise(() => {}));
    instance.client.session.get = vi.fn().mockResolvedValue({ data: sdkSession });

    mockListSessions.mockReturnValue([dbSession] as never);
    mockListInstances.mockReturnValue([instance] as never);
    mockGetWorkspace.mockReturnValue(makeDbWorkspace() as never);

    const orig = process.env.WEAVE_SDK_CALL_TIMEOUT_MS;
    process.env.WEAVE_SDK_CALL_TIMEOUT_MS = "50";
    try {
      const res = await GET(new NextRequest("http://localhost/api/sessions"));
      const body = await res.json();

      // Returns 200 — status timeout is gracefully handled via Promise.allSettled
      expect(res.status).toBe(200);
      expect(body).toHaveLength(1);
      // Status falls back to DB-based value since live status unavailable
      expect(body[0].session.id).toBe("oc-session-abc");
    } finally {
      if (orig !== undefined) {
        process.env.WEAVE_SDK_CALL_TIMEOUT_MS = orig;
      } else {
        delete process.env.WEAVE_SDK_CALL_TIMEOUT_MS;
      }
    }
  });

  it("FallsBackGracefullyWhenSessionListTimesOutDuringDbUnavailable", async () => {
    const instance = makeManagedInstance();
    // session.list never resolves
    instance.client.session.list = vi.fn().mockReturnValue(new Promise(() => {}));

    mockListSessions.mockImplementation(() => {
      throw new Error("DB unavailable");
    });
    mockListInstances.mockReturnValue([instance] as never);

    const orig = process.env.WEAVE_SDK_CALL_TIMEOUT_MS;
    process.env.WEAVE_SDK_CALL_TIMEOUT_MS = "50";
    try {
      const res = await GET(new NextRequest("http://localhost/api/sessions"));
      const body = await res.json();

      // Returns empty array — the timed-out session.list() is caught by Promise.allSettled
      expect(res.status).toBe(200);
      expect(body).toEqual([]);
    } finally {
      if (orig !== undefined) {
        process.env.WEAVE_SDK_CALL_TIMEOUT_MS = orig;
      } else {
        delete process.env.WEAVE_SDK_CALL_TIMEOUT_MS;
      }
    }
  });

  it("DoesNotWriteStatusDuringReadPoll", async () => {
    // GET /api/sessions is read-only: it returns DB status as-is
    // (session-status-watcher keeps DB in sync via SSE events)
    const dbSession = makeDbSession({ status: "idle" });
    const instance = makeManagedInstance();

    mockListSessions.mockReturnValue([dbSession] as never);
    mockListInstances.mockReturnValue([instance] as never);
    mockGetWorkspace.mockReturnValue(makeDbWorkspace() as never);

    const res = await GET(new NextRequest("http://localhost/api/sessions"));
    const body = await res.json();

    expect(res.status).toBe(200);
    // Response reflects DB status directly (no SDK calls)
    expect(body[0].sessionStatus).toBe("idle");
    // Must NOT write to the DB during a read poll
    expect(mockUpdateSessionStatus).not.toHaveBeenCalled();
  });
});

// ─── DELETE /api/sessions/[id] ────────────────────────────────────────────────

describe("DELETE /api/sessions/[id]", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    mockGetSession.mockReturnValue(undefined as never);
    mockGetSessionByOpencodeId.mockReturnValue(undefined as never);
    mockGetSessionsForInstance.mockReturnValue([]);
  });

  it("Returns400WhenInstanceIdQueryParamIsMissing", async () => {
    const req = new NextRequest("http://localhost/api/sessions/session-id", {
      method: "DELETE",
    });
    const context = { params: Promise.resolve({ id: "session-id" }) };

    const res = await DELETE(req, context);
    const body = await res.json();

    expect(res.status).toBe(400);
    expect(body.error).toMatch(/instanceId/i);
  });

  it("Returns200AndCallsDestroyInstanceWhenNoSiblingSessionsExist", async () => {
    mockGetSession.mockReturnValue(makeDbSession() as never);
    mockGetSessionsForInstance.mockReturnValue([]);

    const req = new NextRequest(
      "http://localhost/api/sessions/session-id?instanceId=inst-abc",
      { method: "DELETE" }
    );
    const context = { params: Promise.resolve({ id: "session-id" }) };

    const res = await DELETE(req, context);
    const body = await res.json();

    expect(res.status).toBe(200);
    expect(body.message).toBe("Session terminated");
    expect(mockDestroyInstance).toHaveBeenCalledWith("inst-abc");
  });

  it("DoesNotCallDestroyInstanceWhenSiblingSessionsExist", async () => {
    const dbSession = makeDbSession({ id: "db-sess-1" });
    const sibling = makeDbSession({ id: "db-sess-2", opencode_session_id: "oc-session-xyz" });

    mockGetSession.mockReturnValue(dbSession as never);
    // Return both current + sibling; filter logic removes the current one, leaving sibling
    mockGetSessionsForInstance.mockReturnValue([sibling] as never);

    const req = new NextRequest(
      "http://localhost/api/sessions/session-id?instanceId=inst-abc",
      { method: "DELETE" }
    );
    const context = { params: Promise.resolve({ id: "session-id" }) };

    await DELETE(req, context);

    expect(mockDestroyInstance).not.toHaveBeenCalled();
  });

  it("UpdatesSessionStatusToStoppedWhenActiveInDb", async () => {
    const dbSession = makeDbSession({ id: "db-sess-resolved", status: "active" });
    mockGetSession.mockReturnValue(dbSession as never);
    mockGetSessionsForInstance.mockReturnValue([]);

    const req = new NextRequest(
      "http://localhost/api/sessions/session-id?instanceId=inst-abc",
      { method: "DELETE" }
    );
    const context = { params: Promise.resolve({ id: "session-id" }) };

    await DELETE(req, context);

    expect(mockUpdateSessionStatus).toHaveBeenCalledWith(
      "db-sess-resolved",
      "stopped",
      expect.any(String)
    );
  });

  it("UpdatesSessionStatusToCompletedWhenIdleInDb", async () => {
    const dbSession = makeDbSession({ id: "db-sess-idle", status: "idle" });
    mockGetSession.mockReturnValue(dbSession as never);
    mockGetSessionsForInstance.mockReturnValue([]);

    const req = new NextRequest(
      "http://localhost/api/sessions/session-id?instanceId=inst-abc",
      { method: "DELETE" }
    );
    const context = { params: Promise.resolve({ id: "session-id" }) };

    await DELETE(req, context);

    expect(mockUpdateSessionStatus).toHaveBeenCalledWith(
      "db-sess-idle",
      "completed",
      expect.any(String)
    );
  });

  it("CallsCleanupWorkspaceWhenCleanupWorkspaceParamIsTrue", async () => {
    const dbSession = makeDbSession({ workspace_id: "ws-to-clean" });
    mockGetSession.mockReturnValue(dbSession as never);
    mockGetSessionsForInstance.mockReturnValue([]);
    mockCleanupWorkspace.mockResolvedValue(undefined);

    const req = new NextRequest(
      "http://localhost/api/sessions/session-id?instanceId=inst-abc&cleanupWorkspace=true",
      { method: "DELETE" }
    );
    const context = { params: Promise.resolve({ id: "session-id" }) };

    await DELETE(req, context);

    expect(mockCleanupWorkspace).toHaveBeenCalledWith("ws-to-clean");
  });

  it("DoesNotCallCleanupWorkspaceWhenParamIsNotSet", async () => {
    mockGetSession.mockReturnValue(makeDbSession() as never);
    mockGetSessionsForInstance.mockReturnValue([]);

    const req = new NextRequest(
      "http://localhost/api/sessions/session-id?instanceId=inst-abc",
      { method: "DELETE" }
    );
    const context = { params: Promise.resolve({ id: "session-id" }) };

    await DELETE(req, context);

    expect(mockCleanupWorkspace).not.toHaveBeenCalled();
  });

  it("HandlesDbLookupFailureGracefully", async () => {
    mockGetSession.mockImplementation(() => {
      throw new Error("DB error");
    });
    mockGetSessionsForInstance.mockReturnValue([]);

    const req = new NextRequest(
      "http://localhost/api/sessions/session-id?instanceId=inst-abc",
      { method: "DELETE" }
    );
    const context = { params: Promise.resolve({ id: "session-id" }) };

    const res = await DELETE(req, context);
    const body = await res.json();

    // Should still return 200 — DB failure is non-fatal
    expect(res.status).toBe(200);
    expect(body.message).toBe("Session terminated");
    // updateSessionStatus should NOT be called since resolvedDbId remains null
    expect(mockUpdateSessionStatus).not.toHaveBeenCalled();
  });
});

// ─── PATCH /api/sessions/[id] ─────────────────────────────────────────────────

describe("PATCH /api/sessions/[id]", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    mockGetSession.mockReturnValue(undefined as never);
  });

  it("Returns200WithUpdatedTitleOnSuccess", async () => {
    mockGetSession.mockReturnValue(makeDbSession({ id: "db-sess-1" }) as never);

    const req = new NextRequest("http://localhost/api/sessions/db-sess-1", {
      method: "PATCH",
      body: JSON.stringify({ title: "My Renamed Session" }),
      headers: { "Content-Type": "application/json" },
    });
    const context = { params: Promise.resolve({ id: "db-sess-1" }) };

    const res = await PATCH(req, context);
    const body = await res.json();

    expect(res.status).toBe(200);
    expect(body).toEqual({ id: "db-sess-1", title: "My Renamed Session" });
    expect(mockUpdateSessionTitle).toHaveBeenCalledWith("db-sess-1", "My Renamed Session");
  });

  it("Returns400ForInvalidJsonBody", async () => {
    const req = new NextRequest("http://localhost/api/sessions/db-sess-1", {
      method: "PATCH",
      body: "not-valid-json{{{",
      headers: { "Content-Type": "application/json" },
    });
    const context = { params: Promise.resolve({ id: "db-sess-1" }) };

    const res = await PATCH(req, context);
    const body = await res.json();

    expect(res.status).toBe(400);
    expect(body.error).toMatch(/invalid json/i);
  });

  it("Returns400WhenTitleFieldIsMissing", async () => {
    const req = new NextRequest("http://localhost/api/sessions/db-sess-1", {
      method: "PATCH",
      body: JSON.stringify({ name: "wrong field" }),
      headers: { "Content-Type": "application/json" },
    });
    const context = { params: Promise.resolve({ id: "db-sess-1" }) };

    const res = await PATCH(req, context);
    const body = await res.json();

    expect(res.status).toBe(400);
    expect(body.error).toMatch(/title is required/i);
  });

  it("Returns400WhenTitleIsEmptyString", async () => {
    const req = new NextRequest("http://localhost/api/sessions/db-sess-1", {
      method: "PATCH",
      body: JSON.stringify({ title: "" }),
      headers: { "Content-Type": "application/json" },
    });
    const context = { params: Promise.resolve({ id: "db-sess-1" }) };

    const res = await PATCH(req, context);
    const body = await res.json();

    expect(res.status).toBe(400);
    expect(body.error).toMatch(/title is required/i);
  });

  it("Returns404WhenSessionNotFound", async () => {
    mockGetSession.mockReturnValue(undefined as never);

    const req = new NextRequest("http://localhost/api/sessions/nonexistent", {
      method: "PATCH",
      body: JSON.stringify({ title: "New Title" }),
      headers: { "Content-Type": "application/json" },
    });
    const context = { params: Promise.resolve({ id: "nonexistent" }) };

    const res = await PATCH(req, context);
    const body = await res.json();

    expect(res.status).toBe(404);
    expect(body.error).toMatch(/not found/i);
  });

  it("Returns500WhenDbUpdateFails", async () => {
    mockGetSession.mockReturnValue(makeDbSession({ id: "db-sess-1" }) as never);
    mockUpdateSessionTitle.mockImplementation(() => {
      throw new Error("DB write failed");
    });

    const req = new NextRequest("http://localhost/api/sessions/db-sess-1", {
      method: "PATCH",
      body: JSON.stringify({ title: "New Title" }),
      headers: { "Content-Type": "application/json" },
    });
    const context = { params: Promise.resolve({ id: "db-sess-1" }) };

    const res = await PATCH(req, context);
    const body = await res.json();

    expect(res.status).toBe(500);
    expect(body.error).toMatch(/failed to update/i);
  });
});
