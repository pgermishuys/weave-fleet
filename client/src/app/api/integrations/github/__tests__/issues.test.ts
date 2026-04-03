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

// ─── Imports ──────────────────────────────────────────────────────────────────

import { GET } from "@/app/api/integrations/github/repos/[owner]/[repo]/issues/route";
import * as integrationStore from "@/lib/server/integration-store";

const mockGetConfig = vi.mocked(integrationStore.getIntegrationConfig);

// ─── Tests ────────────────────────────────────────────────────────────────────

async function makeParams(
  owner: string,
  repo: string
): Promise<{ params: Promise<{ owner: string; repo: string }> }> {
  return { params: Promise.resolve({ owner, repo }) };
}

function makeRequest(url: string): NextRequest {
  return new NextRequest(url);
}

describe("GET /api/integrations/github/repos/[owner]/[repo]/issues", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.restoreAllMocks();
  });

  it("Returns401WhenNoTokenConfigured", async () => {
    mockGetConfig.mockReturnValue(null);
    const req = makeRequest(
      "http://localhost/api/integrations/github/repos/acme/my-project/issues"
    );
    const res = await GET(req, await makeParams("acme", "my-project"));
    expect(res.status).toBe(401);
  });

  it("FetchesIssuesFromGitHub", async () => {
    mockGetConfig.mockReturnValue({ token: "ghp_test" });

    const mockIssues = [
      { id: 1, number: 42, title: "Test issue", state: "open" },
    ];
    const mockFetch = vi.spyOn(global, "fetch").mockResolvedValue({
      ok: true,
      status: 200,
      json: async () => mockIssues,
      headers: new Headers(),
    } as Response);

    const req = makeRequest(
      "http://localhost/api/integrations/github/repos/acme/my-project/issues?state=open"
    );
    const res = await GET(req, await makeParams("acme", "my-project"));
    expect(res.status).toBe(200);
    const body = await res.json();
    expect(body).toEqual(mockIssues);

    expect(mockFetch).toHaveBeenCalledWith(
      expect.stringContaining(
        "https://api.github.com/repos/acme/my-project/issues"
      ),
      expect.any(Object)
    );
  });

  it("ForwardsGitHub404", async () => {
    mockGetConfig.mockReturnValue({ token: "ghp_test" });

    vi.spyOn(global, "fetch").mockResolvedValue({
      ok: false,
      status: 404,
      json: async () => ({ message: "Not Found" }),
      headers: new Headers(),
    } as Response);

    const req = makeRequest(
      "http://localhost/api/integrations/github/repos/acme/nonexistent/issues"
    );
    const res = await GET(req, await makeParams("acme", "nonexistent"));
    expect(res.status).toBe(404);
  });

  it("Handles429RateLimitError", async () => {
    mockGetConfig.mockReturnValue({ token: "ghp_test" });

    vi.spyOn(global, "fetch").mockResolvedValue({
      ok: false,
      status: 429,
      json: async () => ({ message: "API rate limit exceeded" }),
      headers: new Headers({ "X-RateLimit-Remaining": "0" }),
    } as Response);

    const req = makeRequest(
      "http://localhost/api/integrations/github/repos/acme/my-project/issues"
    );
    const res = await GET(req, await makeParams("acme", "my-project"));
    expect(res.status).toBe(429);
    const body = await res.json();
    expect(body.error).toBe("API rate limit exceeded");
  });

  it("ForwardsLabelsParamAsCommaSeparatedString", async () => {
    mockGetConfig.mockReturnValue({ token: "ghp_test" });

    const mockFetch = vi.spyOn(global, "fetch").mockResolvedValue({
      ok: true,
      status: 200,
      json: async () => [],
      headers: new Headers(),
    } as Response);

    const req = makeRequest(
      "http://localhost/api/integrations/github/repos/acme/my-project/issues?labels=bug%2Cenhancement"
    );
    await GET(req, await makeParams("acme", "my-project"));

    expect(mockFetch).toHaveBeenCalledWith(
      expect.stringContaining("labels=bug%2Cenhancement"),
      expect.any(Object)
    );
  });

  it("ForwardsMilestoneParam", async () => {
    mockGetConfig.mockReturnValue({ token: "ghp_test" });

    const mockFetch = vi.spyOn(global, "fetch").mockResolvedValue({
      ok: true,
      status: 200,
      json: async () => [],
      headers: new Headers(),
    } as Response);

    const req = makeRequest(
      "http://localhost/api/integrations/github/repos/acme/my-project/issues?milestone=1"
    );
    await GET(req, await makeParams("acme", "my-project"));

    expect(mockFetch).toHaveBeenCalledWith(
      expect.stringContaining("milestone=1"),
      expect.any(Object)
    );
  });

  it("ForwardsAssigneeParam", async () => {
    mockGetConfig.mockReturnValue({ token: "ghp_test" });

    const mockFetch = vi.spyOn(global, "fetch").mockResolvedValue({
      ok: true,
      status: 200,
      json: async () => [],
      headers: new Headers(),
    } as Response);

    const req = makeRequest(
      "http://localhost/api/integrations/github/repos/acme/my-project/issues?assignee=octocat"
    );
    await GET(req, await makeParams("acme", "my-project"));

    expect(mockFetch).toHaveBeenCalledWith(
      expect.stringContaining("assignee=octocat"),
      expect.any(Object)
    );
  });

  it("ForwardsCreatorParam", async () => {
    mockGetConfig.mockReturnValue({ token: "ghp_test" });

    const mockFetch = vi.spyOn(global, "fetch").mockResolvedValue({
      ok: true,
      status: 200,
      json: async () => [],
      headers: new Headers(),
    } as Response);

    const req = makeRequest(
      "http://localhost/api/integrations/github/repos/acme/my-project/issues?creator=alice"
    );
    await GET(req, await makeParams("acme", "my-project"));

    expect(mockFetch).toHaveBeenCalledWith(
      expect.stringContaining("creator=alice"),
      expect.any(Object)
    );
  });

  it("DoesNotForwardAbsentOptionalParams", async () => {
    mockGetConfig.mockReturnValue({ token: "ghp_test" });

    const mockFetch = vi.spyOn(global, "fetch").mockResolvedValue({
      ok: true,
      status: 200,
      json: async () => [],
      headers: new Headers(),
    } as Response);

    const req = makeRequest(
      "http://localhost/api/integrations/github/repos/acme/my-project/issues"
    );
    await GET(req, await makeParams("acme", "my-project"));

    const calledUrl = mockFetch.mock.calls[0][0] as string;
    expect(calledUrl).not.toContain("labels=");
    expect(calledUrl).not.toContain("milestone=");
    expect(calledUrl).not.toContain("assignee=");
    expect(calledUrl).not.toContain("creator=");
    expect(calledUrl).not.toContain("type=");
  });
});
