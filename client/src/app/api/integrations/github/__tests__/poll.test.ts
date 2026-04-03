import { vi, describe, it, expect, beforeEach } from "vitest";
import { NextRequest } from "next/server";

// ─── Mocks ────────────────────────────────────────────────────────────────────

vi.mock("@/lib/server/integration-store", () => ({
  getIntegrationConfig: vi.fn(),
  setIntegrationConfig: vi.fn(),
}));

vi.mock("@/lib/server/logger", () => ({
  log: {
    info: vi.fn(),
    warn: vi.fn(),
    error: vi.fn(),
  },
}));

// ─── Imports ──────────────────────────────────────────────────────────────────

import { POST } from "@/app/api/integrations/github/auth/poll/route";
import * as integrationStore from "@/lib/server/integration-store";
import { GITHUB_OAUTH_CLIENT_ID } from "@/app/api/integrations/github/auth/_config";

const mockSetConfig = vi.mocked(integrationStore.setIntegrationConfig);

// ─── Helpers ──────────────────────────────────────────────────────────────────

function makeRequest(body: unknown): NextRequest {
  return new NextRequest("http://localhost/api/integrations/github/auth/poll", {
    method: "POST",
    body: JSON.stringify(body),
    headers: { "Content-Type": "application/json" },
  });
}

function mockGitHubResponse(data: unknown, ok = true, status = 200) {
  return vi.spyOn(global, "fetch").mockResolvedValue({
    ok,
    status,
    json: async () => data,
  } as Response);
}

// ─── Tests ────────────────────────────────────────────────────────────────────

describe("POST /api/integrations/github/auth/poll", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.restoreAllMocks();
  });

  it("ReturnsCompleteAndStoresTokenOnSuccess", async () => {
    mockGitHubResponse({ access_token: "gho_thetoken123" });
    mockSetConfig.mockReturnValue(true);

    const req = makeRequest({ deviceCode: "dev_code_abc" });
    const res = await POST(req);

    expect(res.status).toBe(200);
    const body = await res.json();
    expect(body.status).toBe("complete");

    expect(mockSetConfig).toHaveBeenCalledWith("github", { token: "gho_thetoken123" });
  });

  it("ReturnsPendingOnAuthorizationPending", async () => {
    mockGitHubResponse({ error: "authorization_pending" });

    const req = makeRequest({ deviceCode: "dev_code_abc" });
    const res = await POST(req);

    expect(res.status).toBe(200);
    const body = await res.json();
    expect(body.status).toBe("pending");
    expect(body.interval).toBeUndefined();
  });

  it("ReturnsPendingWithIncreasedIntervalOnSlowDown", async () => {
    mockGitHubResponse({ error: "slow_down", interval: 15 });

    const req = makeRequest({ deviceCode: "dev_code_abc" });
    const res = await POST(req);

    expect(res.status).toBe(200);
    const body = await res.json();
    expect(body.status).toBe("pending");
    expect(body.interval).toBe(15);
  });

  it("ReturnsPendingWithDefaultIntervalOnSlowDownWithoutServerInterval", async () => {
    mockGitHubResponse({ error: "slow_down" });

    const req = makeRequest({ deviceCode: "dev_code_abc" });
    const res = await POST(req);

    expect(res.status).toBe(200);
    const body = await res.json();
    expect(body.status).toBe("pending");
    expect(body.interval).toBe(10); // default fallback
  });

  it("ReturnsExpiredOnExpiredToken", async () => {
    mockGitHubResponse({ error: "expired_token" });

    const req = makeRequest({ deviceCode: "dev_code_abc" });
    const res = await POST(req);

    expect(res.status).toBe(200);
    const body = await res.json();
    expect(body.status).toBe("expired");
    expect(body.message).toBe("Device code expired. Please restart the flow.");
  });

  it("ReturnsDeniedOnAccessDenied", async () => {
    mockGitHubResponse({ error: "access_denied" });

    const req = makeRequest({ deviceCode: "dev_code_abc" });
    const res = await POST(req);

    expect(res.status).toBe(200);
    const body = await res.json();
    expect(body.status).toBe("denied");
    expect(body.message).toBe("Authorization was denied.");
  });

  it("ReturnsErrorOnUnknownGitHubError", async () => {
    mockGitHubResponse({ error: "some_unexpected_error" });

    const req = makeRequest({ deviceCode: "dev_code_abc" });
    const res = await POST(req);

    expect(res.status).toBe(502);
    const body = await res.json();
    expect(body.status).toBe("error");
    expect(body.message).toBe("some_unexpected_error");
  });

  it("Returns400WhenDeviceCodeMissing", async () => {
    const req = makeRequest({});
    const res = await POST(req);

    expect(res.status).toBe(400);
    const body = await res.json();
    expect(body.error).toBe("deviceCode is required");
  });

  it("Returns502OnNetworkError", async () => {
    vi.spyOn(global, "fetch").mockRejectedValue(new Error("ECONNREFUSED"));

    const req = makeRequest({ deviceCode: "dev_code_abc" });
    const res = await POST(req);

    expect(res.status).toBe(502);
    const body = await res.json();
    expect(body.status).toBe("error");
  });

  it("SendsCorrectGrantTypeToGitHub", async () => {
    const mockFetch = mockGitHubResponse({ access_token: "gho_token" });
    mockSetConfig.mockReturnValue(true);

    const req = makeRequest({ deviceCode: "dev_code_abc" });
    await POST(req);

    expect(mockFetch).toHaveBeenCalledOnce();
    const [, init] = mockFetch.mock.calls[0] as [string, RequestInit];
    const bodyParsed = JSON.parse(init.body as string);

    expect(bodyParsed.grant_type).toBe("urn:ietf:params:oauth:grant-type:device_code");
    expect(bodyParsed.client_id).toBe(GITHUB_OAUTH_CLIENT_ID);
    expect(bodyParsed.device_code).toBe("dev_code_abc");
  });
});
