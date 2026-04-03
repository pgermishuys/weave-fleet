import { vi, describe, it, expect, beforeEach } from "vitest";
import { NextRequest } from "next/server";

vi.mock("@/lib/server/integration-store", () => ({
  getIntegrationConfig: vi.fn(),
}));

vi.mock("@/lib/server/logger", () => ({
  log: { info: vi.fn(), warn: vi.fn(), error: vi.fn() },
}));

import { GET } from "@/app/api/integrations/github/repos/[owner]/[repo]/labels/route";
import * as integrationStore from "@/lib/server/integration-store";

const mockGetConfig = vi.mocked(integrationStore.getIntegrationConfig);

async function makeParams(owner: string, repo: string) {
  return { params: Promise.resolve({ owner, repo }) };
}

describe("GET /api/integrations/github/repos/[owner]/[repo]/labels", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.restoreAllMocks();
  });

  it("Returns401WhenNoTokenConfigured", async () => {
    mockGetConfig.mockReturnValue(null);
    const req = new NextRequest(
      "http://localhost/api/integrations/github/repos/acme/my-project/labels"
    );
    const res = await GET(req, await makeParams("acme", "my-project"));
    expect(res.status).toBe(401);
  });

  it("ReturnsLabelsArray", async () => {
    mockGetConfig.mockReturnValue({ token: "ghp_test" });

    const mockLabels = [
      { name: "bug", color: "d73a4a", description: "Something broken" },
      { name: "enhancement", color: "a2eeef", description: null },
    ];
    vi.spyOn(global, "fetch").mockResolvedValue({
      ok: true,
      status: 200,
      json: async () => mockLabels,
      headers: new Headers(),
    } as Response);

    const req = new NextRequest(
      "http://localhost/api/integrations/github/repos/acme/my-project/labels"
    );
    const res = await GET(req, await makeParams("acme", "my-project"));
    expect(res.status).toBe(200);
    const body = await res.json();
    expect(body).toEqual(mockLabels);
  });

  it("PaginatesUntilExhausted", async () => {
    mockGetConfig.mockReturnValue({ token: "ghp_test" });

    // First page: 100 items (full page), second page: 50 (partial — done)
    const page1 = Array.from({ length: 100 }, (_, i) => ({
      name: `label-${i}`,
      color: "aabbcc",
      description: null,
    }));
    const page2 = Array.from({ length: 50 }, (_, i) => ({
      name: `label-${100 + i}`,
      color: "aabbcc",
      description: null,
    }));

    let callCount = 0;
    vi.spyOn(global, "fetch").mockImplementation(async () => {
      callCount++;
      const data = callCount === 1 ? page1 : page2;
      return {
        ok: true,
        status: 200,
        json: async () => data,
        headers: new Headers(),
      } as Response;
    });

    const req = new NextRequest(
      "http://localhost/api/integrations/github/repos/acme/my-project/labels"
    );
    const res = await GET(req, await makeParams("acme", "my-project"));
    expect(res.status).toBe(200);
    const body = await res.json();
    expect(body).toHaveLength(150);
    expect(callCount).toBe(2);
  });

  it("ForwardsGitHubErrors", async () => {
    mockGetConfig.mockReturnValue({ token: "ghp_test" });
    vi.spyOn(global, "fetch").mockResolvedValue({
      ok: false,
      status: 404,
      json: async () => ({ message: "Not Found" }),
      headers: new Headers(),
    } as Response);

    const req = new NextRequest(
      "http://localhost/api/integrations/github/repos/acme/nonexistent/labels"
    );
    const res = await GET(req, await makeParams("acme", "nonexistent"));
    expect(res.status).toBe(404);
  });
});
