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

import { POST } from "@/app/api/integrations/github/auth/device-code/route";
import { GITHUB_OAUTH_CLIENT_ID, OAUTH_SCOPES } from "@/app/api/integrations/github/auth/_config";

// ─── Helpers ──────────────────────────────────────────────────────────────────

function makeGitHubDeviceCodeResponse() {
  return {
    device_code: "abc123devicecode",
    user_code: "WDJB-MJHT",
    verification_uri: "https://github.com/login/device",
    expires_in: 900,
    interval: 5,
  };
}

function makeRequest(): NextRequest {
  return new NextRequest("http://localhost/api/integrations/github/auth/device-code", {
    method: "POST",
  });
}

// ─── Tests ────────────────────────────────────────────────────────────────────

describe("POST /api/integrations/github/auth/device-code", () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.restoreAllMocks();
  });

  it("ReturnsUserCodeAndVerificationUriOnSuccess", async () => {
    vi.spyOn(global, "fetch").mockResolvedValue({
      ok: true,
      status: 200,
      json: async () => makeGitHubDeviceCodeResponse(),
    } as Response);

    const res = await POST();
    expect(res.status).toBe(200);
    const body = await res.json();
    expect(body).toEqual({
      userCode: "WDJB-MJHT",
      verificationUri: "https://github.com/login/device",
      deviceCode: "abc123devicecode",
      expiresIn: 900,
      interval: 5,
    });
  });

  it("Returns502WhenGitHubReturnsNon200", async () => {
    vi.spyOn(global, "fetch").mockResolvedValue({
      ok: false,
      status: 422,
      json: async () => ({ message: "Validation Failed" }),
    } as Response);

    const res = await POST();
    expect(res.status).toBe(502);
    const body = await res.json();
    expect(body.error).toBe("Failed to initiate GitHub device authorization");
  });

  it("Returns502OnNetworkError", async () => {
    vi.spyOn(global, "fetch").mockRejectedValue(new Error("ECONNREFUSED"));

    const res = await POST();
    expect(res.status).toBe(502);
    const body = await res.json();
    expect(body.error).toBe("Failed to initiate GitHub device authorization");
  });

  it("SendsCorrectHeadersAndBodyToGitHub", async () => {
    const mockFetch = vi.spyOn(global, "fetch").mockResolvedValue({
      ok: true,
      status: 200,
      json: async () => makeGitHubDeviceCodeResponse(),
    } as Response);

    await POST();

    expect(mockFetch).toHaveBeenCalledOnce();
    const [url, init] = mockFetch.mock.calls[0] as [string, RequestInit];

    expect(url).toBe("https://github.com/login/device/code");
    expect(init.method).toBe("POST");

    const headers = init.headers as Record<string, string>;
    expect(headers["Accept"]).toBe("application/json");
    expect(headers["Content-Type"]).toBe("application/json");
    expect(headers["User-Agent"]).toBe("weave-agent-fleet");

    const bodyParsed = JSON.parse(init.body as string);
    expect(bodyParsed.client_id).toBe(GITHUB_OAUTH_CLIENT_ID);
    expect(bodyParsed.scope).toBe(OAUTH_SCOPES);
  });

  it("DoesNotExposeSensitiveDataOnParseError", async () => {
    vi.spyOn(global, "fetch").mockResolvedValue({
      ok: true,
      status: 200,
      json: async () => { throw new Error("Invalid JSON"); },
    } as unknown as Response);

    const res = await POST();
    expect(res.status).toBe(502);
    const body = await res.json();
    expect(body.error).toBe("Failed to initiate GitHub device authorization");
  });
});
