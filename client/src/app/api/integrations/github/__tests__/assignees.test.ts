import { vi, describe, it, expect, beforeEach } from "vitest";
import { NextRequest } from "next/server";

vi.mock("@/lib/server/integration-store", () => ({
  getIntegrationConfig: vi.fn(),
}));

vi.mock("@/lib/server/logger", () => ({
  log: { info: vi.fn(), warn: vi.fn(), error: vi.fn() },
}));

import { GET } from "@/app/api/integrations/github/repos/[owner]/[repo]/assignees/route";
import * as integrationStore from "@/lib/server/integration-store";

const mockGetConfig = vi.mocked(integrationStore.getIntegrationConfig);

async function makeParams(owner: string, repo: string) {
  return { params: Promise.resolve({ owner, repo }) };
}

describe("GET /api/integrations/github/repos/[owner]/[repo]/assignees", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.restoreAllMocks();
  });

  it("Returns401WhenNoTokenConfigured", async () => {
    mockGetConfig.mockReturnValue(null);
    const req = new NextRequest(
      "http://localhost/api/integrations/github/repos/acme/my-project/assignees"
    );
    const res = await GET(req, await makeParams("acme", "my-project"));
    expect(res.status).toBe(401);
  });

  it("ReturnsAssigneesArray", async () => {
    mockGetConfig.mockReturnValue({ token: "ghp_test" });

    const mockAssignees = [
      { login: "octocat", avatar_url: "https://avatars.githubusercontent.com/u/1" },
      { login: "hubot", avatar_url: "https://avatars.githubusercontent.com/u/2" },
    ];
    vi.spyOn(global, "fetch").mockResolvedValue({
      ok: true,
      status: 200,
      json: async () => mockAssignees,
      headers: new Headers(),
    } as Response);

    const req = new NextRequest(
      "http://localhost/api/integrations/github/repos/acme/my-project/assignees"
    );
    const res = await GET(req, await makeParams("acme", "my-project"));
    expect(res.status).toBe(200);
    const body = await res.json();
    expect(body).toEqual(mockAssignees);
  });
});
