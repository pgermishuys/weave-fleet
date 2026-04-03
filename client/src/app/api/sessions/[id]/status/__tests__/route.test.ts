import { vi } from "vitest";
import { NextRequest } from "next/server";

// ─── Mocks ───────────────────────────────────────────────────────────────────

vi.mock("@/lib/server/process-manager", () => ({
  getInstance: vi.fn(),
  _recoveryComplete: Promise.resolve(),
}));

vi.mock("@/lib/server/opencode-client", () => ({
  getClientForInstance: vi.fn(),
}));

// ─── Imports (after mocks) ────────────────────────────────────────────────────

import { GET } from "@/app/api/sessions/[id]/status/route";
import * as processManager from "@/lib/server/process-manager";
import * as opencodeClient from "@/lib/server/opencode-client";

const mockGetInstance = vi.mocked(processManager.getInstance);
const mockGetClientForInstance = vi.mocked(opencodeClient.getClientForInstance);

// ─── Helpers ──────────────────────────────────────────────────────────────────

function makeRequest(sessionId: string, instanceId?: string): NextRequest {
  const params = instanceId ? `?instanceId=${encodeURIComponent(instanceId)}` : "";
  return new NextRequest(
    `http://localhost/api/sessions/${encodeURIComponent(sessionId)}/status${params}`,
    { method: "GET" }
  );
}

function makeContext(sessionId: string) {
  return { params: Promise.resolve({ id: sessionId }) };
}

function makeInstance(overrides: Record<string, unknown> = {}) {
  return {
    id: "inst-abc",
    directory: "/home/user/project",
    status: "running" as const,
    client: {
      session: {
        status: vi.fn(),
      },
    },
    ...overrides,
  };
}

// ─── Tests ────────────────────────────────────────────────────────────────────

describe("GET /api/sessions/[id]/status", () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it("Returns400WhenInstanceIdQueryParamIsMissing", async () => {
    const req = makeRequest("sess-1");
    const ctx = makeContext("sess-1");

    const res = await GET(req, ctx);
    const body = await res.json();

    expect(res.status).toBe(400);
    expect(body.error).toMatch(/instanceId/i);
  });

  it("Returns404WhenInstanceIsNotFound", async () => {
    mockGetInstance.mockReturnValue(undefined);

    const req = makeRequest("sess-1", "inst-missing");
    const ctx = makeContext("sess-1");

    const res = await GET(req, ctx);
    const body = await res.json();

    expect(res.status).toBe(404);
    expect(body.error).toMatch(/not found/i);
  });

  it("Returns404WhenInstanceIsDead", async () => {
    mockGetInstance.mockReturnValue(makeInstance({ status: "dead" }) as never);

    const req = makeRequest("sess-1", "inst-dead");
    const ctx = makeContext("sess-1");

    const res = await GET(req, ctx);
    const body = await res.json();

    expect(res.status).toBe(404);
    expect(body.error).toMatch(/not found|unavailable/i);
  });

  it("Returns404WhenGetClientForInstanceThrows", async () => {
    mockGetInstance.mockReturnValue(makeInstance() as never);
    mockGetClientForInstance.mockImplementation(() => {
      throw new Error("Instance is dead");
    });

    const req = makeRequest("sess-1", "inst-abc");
    const ctx = makeContext("sess-1");

    const res = await GET(req, ctx);
    const body = await res.json();

    expect(res.status).toBe(404);
    expect(body.error).toMatch(/not found|unavailable/i);
  });

  it("ReturnsBusyWhenSdkStatusMapContainsSessionWithTypeBusy", async () => {
    const instance = makeInstance();
    mockGetInstance.mockReturnValue(instance as never);
    const mockClient = {
      session: {
        status: vi.fn().mockResolvedValue({
          data: { "sess-1": { type: "busy" } },
        }),
      },
    };
    mockGetClientForInstance.mockReturnValue(mockClient as never);

    const req = makeRequest("sess-1", "inst-abc");
    const ctx = makeContext("sess-1");

    const res = await GET(req, ctx);
    const body = await res.json();

    expect(res.status).toBe(200);
    expect(body.status).toBe("busy");
  });

  it("ReturnsBusyWhenSdkStatusMapContainsSessionWithTypeRetry", async () => {
    const instance = makeInstance();
    mockGetInstance.mockReturnValue(instance as never);
    const mockClient = {
      session: {
        status: vi.fn().mockResolvedValue({
          data: { "sess-1": { type: "retry" } },
        }),
      },
    };
    mockGetClientForInstance.mockReturnValue(mockClient as never);

    const req = makeRequest("sess-1", "inst-abc");
    const ctx = makeContext("sess-1");

    const res = await GET(req, ctx);
    const body = await res.json();

    expect(res.status).toBe(200);
    expect(body.status).toBe("busy");
  });

  it("ReturnsIdleWhenSessionIsAbsentFromSdkStatusMap", async () => {
    const instance = makeInstance();
    mockGetInstance.mockReturnValue(instance as never);
    const mockClient = {
      session: {
        status: vi.fn().mockResolvedValue({
          data: { "other-session": { type: "busy" } },
        }),
      },
    };
    mockGetClientForInstance.mockReturnValue(mockClient as never);

    const req = makeRequest("sess-1", "inst-abc");
    const ctx = makeContext("sess-1");

    const res = await GET(req, ctx);
    const body = await res.json();

    expect(res.status).toBe(200);
    expect(body.status).toBe("idle");
  });

  it("ReturnsIdleWhenSdkStatusMapIsEmpty", async () => {
    const instance = makeInstance();
    mockGetInstance.mockReturnValue(instance as never);
    const mockClient = {
      session: {
        status: vi.fn().mockResolvedValue({ data: {} }),
      },
    };
    mockGetClientForInstance.mockReturnValue(mockClient as never);

    const req = makeRequest("sess-1", "inst-abc");
    const ctx = makeContext("sess-1");

    const res = await GET(req, ctx);
    const body = await res.json();

    expect(res.status).toBe(200);
    expect(body.status).toBe("idle");
  });

  it("ReturnsIdleWhenSdkReturnsNullData", async () => {
    const instance = makeInstance();
    mockGetInstance.mockReturnValue(instance as never);
    const mockClient = {
      session: {
        status: vi.fn().mockResolvedValue({ data: null }),
      },
    };
    mockGetClientForInstance.mockReturnValue(mockClient as never);

    const req = makeRequest("sess-1", "inst-abc");
    const ctx = makeContext("sess-1");

    const res = await GET(req, ctx);
    const body = await res.json();

    expect(res.status).toBe(200);
    expect(body.status).toBe("idle");
  });

  it("Returns500WhenSdkCallFails", async () => {
    const instance = makeInstance();
    mockGetInstance.mockReturnValue(instance as never);
    const mockClient = {
      session: {
        status: vi.fn().mockRejectedValue(new Error("SDK timeout")),
      },
    };
    mockGetClientForInstance.mockReturnValue(mockClient as never);

    const req = makeRequest("sess-1", "inst-abc");
    const ctx = makeContext("sess-1");

    const res = await GET(req, ctx);
    const body = await res.json();

    expect(res.status).toBe(500);
    expect(body.error).toMatch(/failed to fetch/i);
  });
});
