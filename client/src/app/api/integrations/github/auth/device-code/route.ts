import { NextResponse } from "next/server";
import { log } from "@/lib/server/logger";
import {
  GITHUB_OAUTH_CLIENT_ID,
  GITHUB_DEVICE_CODE_URL,
  OAUTH_SCOPES,
} from "../_config";
import type {
  DeviceCodeResponse,
  GitHubDeviceCodeResponse,
} from "../_types";

/**
 * POST /api/integrations/github/auth/device-code
 *
 * Initiates the GitHub Device Authorization flow (RFC 8628).
 * Calls GitHub's device code endpoint and returns the user-facing
 * code and verification URL to the frontend.
 *
 * The raw `device_code` is forwarded to the client so it can be echoed
 * back to the /poll route. This is safe — `device_code` alone cannot
 * obtain a token without the server-side `client_id`.
 */
export async function POST(): Promise<NextResponse> {
  let response: Response;
  try {
    response = await fetch(GITHUB_DEVICE_CODE_URL, {
      method: "POST",
      headers: {
        Accept: "application/json",
        "Content-Type": "application/json",
        "User-Agent": "weave-agent-fleet",
      },
      body: JSON.stringify({
        client_id: GITHUB_OAUTH_CLIENT_ID,
        scope: OAUTH_SCOPES,
      }),
    });
  } catch (err) {
    log.warn("github-device-code", "Network error calling GitHub device code endpoint", { err });
    return NextResponse.json(
      { error: "Failed to initiate GitHub device authorization" },
      { status: 502 }
    );
  }

  if (!response.ok) {
    log.warn("github-device-code", "GitHub returned non-200 for device code request", {
      status: response.status,
    });
    return NextResponse.json(
      { error: "Failed to initiate GitHub device authorization" },
      { status: 502 }
    );
  }

  let data: GitHubDeviceCodeResponse;
  try {
    data = (await response.json()) as GitHubDeviceCodeResponse;
  } catch (err) {
    log.warn("github-device-code", "Failed to parse GitHub device code response", { err });
    return NextResponse.json(
      { error: "Failed to initiate GitHub device authorization" },
      { status: 502 }
    );
  }

  const result: DeviceCodeResponse = {
    userCode: data.user_code,
    verificationUri: data.verification_uri,
    deviceCode: data.device_code,
    expiresIn: data.expires_in,
    interval: data.interval,
  };

  return NextResponse.json(result);
}
