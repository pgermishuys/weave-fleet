import { vi, describe, it, expect, beforeEach } from "vitest";
import { NextRequest } from "next/server";

// ─── Mocks ────────────────────────────────────────────────────────────────────

vi.mock("@/lib/server/integration-store", () => ({
  getIntegrationConfig: vi.fn(),
}));

vi.mock("@/lib/server/logger", () => ({
  log: {
    info: vi.fn(),
    warn: vi.fn(),
    error: vi.fn(),
  },
}));

vi.mock("node-fetch", () => ({ default: vi.fn() }));

// ─── Imports ──────────────────────────────────────────────────────────────────

import { GET } from "@/app/api/integrations/github/repos/route";
import * as integrationStore from "@/lib/server/integration-store";

const mockGetConfig = vi.mocked(integrationStore.getIntegrationConfig);

// ─── Tests ────────────────────────────────────────────────────────────────────

function makeRequest(url: string): NextRequest {
  return new NextRequest(url);
}

describe("GET /api/integrations/github/repos", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.restoreAllMocks();
  });

  it("Returns401WhenNoTokenConfigured", async () => {
    mockGetConfig.mockReturnValue(null);
    const req = makeRequest("http://localhost/api/integrations/github/repos");
    const res = await GET(req);
    expect(res.status).toBe(401);
    const body = await res.json();
    expect(body.error).toBe("GitHub not connected");
  });

  it("Returns401WhenTokenMissing", async () => {
    mockGetConfig.mockReturnValue({ connectedAt: "2025-01-01" });
    const req = makeRequest("http://localhost/api/integrations/github/repos");
    const res = await GET(req);
    expect(res.status).toBe(401);
  });

  it("ForwardsToGitHubAPIAndReturnsData", async () => {
    mockGetConfig.mockReturnValue({ token: "ghp_test" });

    const mockRepos = [{ id: 1, full_name: "acme/my-project" }];
    const mockFetch = vi.spyOn(global, "fetch").mockResolvedValue({
      ok: true,
      status: 200,
      json: async () => mockRepos,
      headers: new Headers({
        "X-RateLimit-Remaining": "4999",
        "X-RateLimit-Reset": "1735689600",
      }),
    } as Response);

    const req = makeRequest(
      "http://localhost/api/integrations/github/repos?per_page=10"
    );
    const res = await GET(req);
    expect(res.status).toBe(200);
    const body = await res.json();
    expect(body).toEqual(mockRepos);

    expect(mockFetch).toHaveBeenCalledWith(
      expect.stringContaining("https://api.github.com/user/repos"),
      expect.objectContaining({
        headers: expect.objectContaining({
          Authorization: "Bearer ghp_test",
        }),
      })
    );
  });

  it("ForwardsGitHubErrorResponse", async () => {
    mockGetConfig.mockReturnValue({ token: "ghp_test" });

    vi.spyOn(global, "fetch").mockResolvedValue({
      ok: false,
      status: 401,
      json: async () => ({ message: "Bad credentials" }),
      headers: new Headers(),
    } as Response);

    const req = makeRequest("http://localhost/api/integrations/github/repos");
    const res = await GET(req);
    expect(res.status).toBe(401);
    const body = await res.json();
    expect(body.error).toBe("Bad credentials");
  });

  it("Returns502OnNetworkError", async () => {
    mockGetConfig.mockReturnValue({ token: "ghp_test" });

    vi.spyOn(global, "fetch").mockRejectedValue(
      new Error("ECONNREFUSED")
    );

    const req = makeRequest("http://localhost/api/integrations/github/repos");
    const res = await GET(req);
    expect(res.status).toBe(502);
    const body = await res.json();
    expect(body.error).toBe("Network error");
  });
});
