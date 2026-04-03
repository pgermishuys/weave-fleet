import { NextRequest, NextResponse } from "next/server";
import { log } from "@/lib/server/logger";
import { setIntegrationConfig } from "@/lib/server/integration-store";
import {
  GITHUB_OAUTH_CLIENT_ID,
  GITHUB_ACCESS_TOKEN_URL,
} from "../_config";
import type { PollRequest, PollResponse, GitHubAccessTokenResponse } from "../_types";

/**
 * POST /api/integrations/github/auth/poll
 *
 * Polls GitHub for an access token using the device code returned by the
 * device-code route. Stateless — each call is independent; the client
 * drives the polling interval.
 *
 * Handles all RFC 8628 §3.5 error states:
 *   - authorization_pending → user hasn't authorized yet, keep polling
 *   - slow_down             → must increase polling interval by 5 seconds
 *   - expired_token         → device code expired, restart the flow
 *   - access_denied         → user denied authorization
 */
export async function POST(request: NextRequest): Promise<NextResponse> {
  let body: PollRequest;
  try {
    body = (await request.json()) as PollRequest;
  } catch {
    return NextResponse.json({ error: "Invalid JSON body" }, { status: 400 });
  }

  const { deviceCode } = body;

  if (!deviceCode || typeof deviceCode !== "string" || !deviceCode.trim()) {
    return NextResponse.json(
      { error: "deviceCode is required" },
      { status: 400 }
    );
  }

  let response: Response;
  try {
    response = await fetch(GITHUB_ACCESS_TOKEN_URL, {
      method: "POST",
      headers: {
        Accept: "application/json",
        "Content-Type": "application/json",
        "User-Agent": "weave-agent-fleet",
      },
      body: JSON.stringify({
        client_id: GITHUB_OAUTH_CLIENT_ID,
        device_code: deviceCode,
        grant_type: "urn:ietf:params:oauth:grant-type:device_code",
      }),
    });
  } catch (err) {
    log.warn("github-auth-poll", "Network error calling GitHub access token endpoint", { err });
    return NextResponse.json(
      { status: "error", message: "GitHub API error" } satisfies PollResponse,
      { status: 502 }
    );
  }

  if (!response.ok) {
    log.warn("github-auth-poll", "GitHub returned non-200 for access token request", {
      status: response.status,
    });
    return NextResponse.json(
      { status: "error", message: "GitHub API error" } satisfies PollResponse,
      { status: 502 }
    );
  }

  let data: GitHubAccessTokenResponse;
  try {
    data = (await response.json()) as GitHubAccessTokenResponse;
  } catch (err) {
    log.warn("github-auth-poll", "Failed to parse GitHub access token response", { err });
    return NextResponse.json(
      { status: "error", message: "GitHub API error" } satisfies PollResponse,
      { status: 502 }
    );
  }

  // Success — store the token and return complete
  if (data.access_token) {
    setIntegrationConfig("github", { token: data.access_token });
    return NextResponse.json({ status: "complete" } satisfies PollResponse);
  }

  // RFC 8628 §3.5 error states
  if (data.error === "authorization_pending") {
    return NextResponse.json({ status: "pending" } satisfies PollResponse);
  }

  if (data.error === "slow_down") {
    // RFC 8628 §3.5: on slow_down, add 5 seconds to the current interval.
    // Use server-provided interval if available, otherwise add 5 to whatever
    // the client last used (we don't track it server-side, so default to 10).
    const updatedInterval =
      typeof data.interval === "number" && data.interval > 0
        ? data.interval
        : 10;
    return NextResponse.json(
      { status: "pending", interval: updatedInterval } satisfies PollResponse
    );
  }

  if (data.error === "expired_token") {
    return NextResponse.json(
      {
        status: "expired",
        message: "Device code expired. Please restart the flow.",
      } satisfies PollResponse
    );
  }

  if (data.error === "access_denied") {
    return NextResponse.json(
      {
        status: "denied",
        message: "Authorization was denied.",
      } satisfies PollResponse
    );
  }

  if (data.error) {
    log.warn("github-auth-poll", "Unknown error from GitHub access token endpoint", {
      error: data.error,
    });
    return NextResponse.json(
      { status: "error", message: data.error } satisfies PollResponse,
      { status: 502 }
    );
  }

  // Unexpected: no access_token and no error — treat as pending
  return NextResponse.json({ status: "pending" } satisfies PollResponse);
}
