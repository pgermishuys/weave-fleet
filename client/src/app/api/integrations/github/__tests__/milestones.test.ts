import { vi, describe, it, expect, beforeEach } from "vitest";
import { NextRequest } from "next/server";

vi.mock("@/lib/server/integration-store", () => ({
  getIntegrationConfig: vi.fn(),
}));

vi.mock("@/lib/server/logger", () => ({
  log: { info: vi.fn(), warn: vi.fn(), error: vi.fn() },
}));

import { GET } from "@/app/api/integrations/github/repos/[owner]/[repo]/milestones/route";
import * as integrationStore from "@/lib/server/integration-store";

const mockGetConfig = vi.mocked(integrationStore.getIntegrationConfig);

async function makeParams(owner: string, repo: string) {
  return { params: Promise.resolve({ owner, repo }) };
}

describe("GET /api/integrations/github/repos/[owner]/[repo]/milestones", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.restoreAllMocks();
  });

  it("Returns401WhenNoTokenConfigured", async () => {
    mockGetConfig.mockReturnValue(null);
    const req = new NextRequest(
      "http://localhost/api/integrations/github/repos/acme/my-project/milestones"
    );
    const res = await GET(req, await makeParams("acme", "my-project"));
    expect(res.status).toBe(401);
  });

  it("ReturnsMilestonesArray", async () => {
    mockGetConfig.mockReturnValue({ token: "ghp_test" });

    const mockMilestones = [
      { number: 1, title: "v1.0", state: "open", open_issues: 5, closed_issues: 10 },
      { number: 2, title: "v2.0", state: "open", open_issues: 3, closed_issues: 0 },
    ];
    vi.spyOn(global, "fetch").mockResolvedValue({
      ok: true,
      status: 200,
      json: async () => mockMilestones,
      headers: new Headers(),
    } as Response);

    const req = new NextRequest(
      "http://localhost/api/integrations/github/repos/acme/my-project/milestones"
    );
    const res = await GET(req, await makeParams("acme", "my-project"));
    expect(res.status).toBe(200);
    const body = await res.json();
    expect(body).toEqual(mockMilestones);
  });

  it("RequestsOpenMilestonesWithPerPage100", async () => {
    mockGetConfig.mockReturnValue({ token: "ghp_test" });

    const mockFetch = vi.spyOn(global, "fetch").mockResolvedValue({
      ok: true,
      status: 200,
      json: async () => [],
      headers: new Headers(),
    } as Response);

    const req = new NextRequest(
      "http://localhost/api/integrations/github/repos/acme/my-project/milestones"
    );
    await GET(req, await makeParams("acme", "my-project"));

    expect(mockFetch).toHaveBeenCalledWith(
      expect.stringContaining("state=open"),
      expect.any(Object)
    );
    expect(mockFetch).toHaveBeenCalledWith(
      expect.stringContaining("per_page=100"),
      expect.any(Object)
    );
  });
});
