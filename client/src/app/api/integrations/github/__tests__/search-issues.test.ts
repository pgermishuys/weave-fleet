import { vi, describe, it, expect, beforeEach } from "vitest";
import { NextRequest } from "next/server";

vi.mock("@/lib/server/integration-store", () => ({
  getIntegrationConfig: vi.fn(),
}));

vi.mock("@/lib/server/logger", () => ({
  log: { info: vi.fn(), warn: vi.fn(), error: vi.fn() },
}));

import { GET } from "@/app/api/integrations/github/repos/[owner]/[repo]/issues/search/route";
import * as integrationStore from "@/lib/server/integration-store";

const mockGetConfig = vi.mocked(integrationStore.getIntegrationConfig);

async function makeParams(owner: string, repo: string) {
  return { params: Promise.resolve({ owner, repo }) };
}

describe("GET /api/integrations/github/repos/[owner]/[repo]/issues/search", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.restoreAllMocks();
  });

  it("Returns401WhenNoTokenConfigured", async () => {
    mockGetConfig.mockReturnValue(null);
    const req = new NextRequest(
      "http://localhost/api/integrations/github/repos/acme/my-project/issues/search?q=bug"
    );
    const res = await GET(req, await makeParams("acme", "my-project"));
    expect(res.status).toBe(401);
  });

  it("ConstructsSearchQueryWithRepoAndTypeQualifiers", async () => {
    mockGetConfig.mockReturnValue({ token: "ghp_test" });

    const mockSearchResult = {
      total_count: 1,
      incomplete_results: false,
      items: [{ id: 1, number: 42, title: "Fix bug", state: "open" }],
    };
    const mockFetch = vi.spyOn(global, "fetch").mockResolvedValue({
      ok: true,
      status: 200,
      json: async () => mockSearchResult,
      headers: new Headers(),
    } as Response);

    const req = new NextRequest(
      "http://localhost/api/integrations/github/repos/acme/my-project/issues/search?q=fix+bug"
    );
    const res = await GET(req, await makeParams("acme", "my-project"));
    expect(res.status).toBe(200);

    // Check the search API was called with repo: and type:issue qualifiers
    const calledUrl = mockFetch.mock.calls[0][0] as string;
    expect(calledUrl).toContain("api.github.com/search/issues");
    expect(calledUrl).toContain("repo%3Aacme%2Fmy-project");
    expect(calledUrl).toContain("type%3Aissue");
  });

  it("ReturnsTotalCountAndItems", async () => {
    mockGetConfig.mockReturnValue({ token: "ghp_test" });

    const mockSearchResult = {
      total_count: 42,
      incomplete_results: false,
      items: [{ id: 1, number: 7, title: "Crash on startup", state: "open" }],
    };
    vi.spyOn(global, "fetch").mockResolvedValue({
      ok: true,
      status: 200,
      json: async () => mockSearchResult,
      headers: new Headers(),
    } as Response);

    const req = new NextRequest(
      "http://localhost/api/integrations/github/repos/acme/my-project/issues/search?q=crash"
    );
    const res = await GET(req, await makeParams("acme", "my-project"));
    const body = await res.json();
    expect(body.total_count).toBe(42);
    expect(body.items).toHaveLength(1);
  });

  it("Returns422ForInvalidQuery", async () => {
    mockGetConfig.mockReturnValue({ token: "ghp_test" });

    vi.spyOn(global, "fetch").mockResolvedValue({
      ok: false,
      status: 422,
      json: async () => ({ message: "Validation Failed" }),
      headers: new Headers(),
    } as Response);

    const req = new NextRequest(
      "http://localhost/api/integrations/github/repos/acme/my-project/issues/search?q=INVALID::QUERY"
    );
    const res = await GET(req, await makeParams("acme", "my-project"));
    expect(res.status).toBe(422);
    const body = await res.json();
    expect(body.error).toBe("Invalid search query");
  });

  it("WorksWithEmptyQuery", async () => {
    mockGetConfig.mockReturnValue({ token: "ghp_test" });

    const mockSearchResult = { total_count: 5, incomplete_results: false, items: [] };
    const mockFetch = vi.spyOn(global, "fetch").mockResolvedValue({
      ok: true,
      status: 200,
      json: async () => mockSearchResult,
      headers: new Headers(),
    } as Response);

    const req = new NextRequest(
      "http://localhost/api/integrations/github/repos/acme/my-project/issues/search"
    );
    await GET(req, await makeParams("acme", "my-project"));

    // Even with no q param, should still include repo: and type:issue
    const calledUrl = mockFetch.mock.calls[0][0] as string;
    expect(calledUrl).toContain("repo%3Aacme%2Fmy-project");
  });
});
