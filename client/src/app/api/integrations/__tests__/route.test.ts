import { vi, describe, it, expect, beforeEach } from "vitest";
import { NextRequest } from "next/server";

// ─── Mocks ────────────────────────────────────────────────────────────────────

vi.mock("@/lib/server/integration-store", () => ({
  getAllIntegrationConfigs: vi.fn(),
  setIntegrationConfig: vi.fn(),
  removeIntegrationConfig: vi.fn(),
}));

vi.mock("@/lib/server/logger", () => ({
  log: {
    info: vi.fn(),
    warn: vi.fn(),
    error: vi.fn(),
  },
}));

// ─── Imports (after mocks) ────────────────────────────────────────────────────

import { GET, POST, DELETE } from "@/app/api/integrations/route";
import * as integrationStore from "@/lib/server/integration-store";

const mockGetAll = vi.mocked(integrationStore.getAllIntegrationConfigs);
const mockSet = vi.mocked(integrationStore.setIntegrationConfig);
const mockRemove = vi.mocked(integrationStore.removeIntegrationConfig);

// ─── Helpers ─────────────────────────────────────────────────────────────────

function makeRequest(
  url: string,
  options?: { method?: string; body?: unknown }
): NextRequest {
  return new NextRequest(url, {
    method: options?.method ?? "GET",
    body: options?.body ? JSON.stringify(options.body) : undefined,
    headers: options?.body
      ? { "Content-Type": "application/json" }
      : undefined,
  });
}

// ─── Tests ────────────────────────────────────────────────────────────────────

describe("GET /api/integrations", () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it("ReturnsEmptyListWhenNoIntegrationsConfigured", async () => {
    mockGetAll.mockReturnValue({});
    const res = await GET();
    const body = await res.json();
    expect(res.status).toBe(200);
    expect(body.integrations).toEqual([]);
  });

  it("ReturnsConnectedIntegrations", async () => {
    mockGetAll.mockReturnValue({
      github: { token: "ghp_test", connectedAt: "2025-01-01T00:00:00.000Z" },
    });
    const res = await GET();
    const body = await res.json();
    expect(res.status).toBe(200);
    expect(body.integrations).toHaveLength(1);
    expect(body.integrations[0]).toMatchObject({
      id: "github",
      name: "GitHub",
      status: "connected",
      connectedAt: "2025-01-01T00:00:00.000Z",
    });
  });
});

describe("POST /api/integrations", () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it("ReturnsBadRequestWithoutId", async () => {
    const req = makeRequest("http://localhost/api/integrations", {
      method: "POST",
      body: { config: { token: "ghp_test" } },
    });
    const res = await POST(req);
    expect(res.status).toBe(400);
  });

  it("ReturnsBadRequestWithoutConfig", async () => {
    const req = makeRequest("http://localhost/api/integrations", {
      method: "POST",
      body: { id: "github" },
    });
    const res = await POST(req);
    expect(res.status).toBe(400);
  });

  it("SavesIntegrationConfig", async () => {
    mockSet.mockReturnValue(true);
    const req = makeRequest("http://localhost/api/integrations", {
      method: "POST",
      body: { id: "github", config: { token: "ghp_test" } },
    });
    const res = await POST(req);
    const body = await res.json();
    expect(res.status).toBe(200);
    expect(body.success).toBe(true);
    expect(mockSet).toHaveBeenCalledWith("github", { token: "ghp_test" });
  });

  it("Returns500WhenStoreFails", async () => {
    mockSet.mockReturnValue(false);
    const req = makeRequest("http://localhost/api/integrations", {
      method: "POST",
      body: { id: "github", config: { token: "ghp_test" } },
    });
    const res = await POST(req);
    expect(res.status).toBe(500);
  });
});

describe("DELETE /api/integrations", () => {
  beforeEach(() => {
    vi.clearAllMocks();
  });

  it("ReturnsBadRequestWithoutId", async () => {
    const req = makeRequest("http://localhost/api/integrations", {
      method: "DELETE",
    });
    const res = await DELETE(req);
    expect(res.status).toBe(400);
  });

  it("RemovesIntegrationConfig", async () => {
    mockRemove.mockReturnValue(true);
    const req = makeRequest("http://localhost/api/integrations?id=github", {
      method: "DELETE",
    });
    const res = await DELETE(req);
    const body = await res.json();
    expect(res.status).toBe(200);
    expect(body.success).toBe(true);
    expect(mockRemove).toHaveBeenCalledWith("github");
  });

  it("Returns500WhenStoreFails", async () => {
    mockRemove.mockReturnValue(false);
    const req = makeRequest("http://localhost/api/integrations?id=github", {
      method: "DELETE",
    });
    const res = await DELETE(req);
    expect(res.status).toBe(500);
  });
});
