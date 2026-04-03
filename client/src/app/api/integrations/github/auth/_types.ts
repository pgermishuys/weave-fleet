/**
 * Shared TypeScript types for the GitHub Device Authorization flow (RFC 8628).
 * Used by both the API routes and the frontend settings component.
 *
 * Client-facing types are now in @/lib/api-types to support static export.
 * This file re-exports them for backward compatibility with server-side code.
 */

export type { DeviceCodeResponse, PollRequest, PollResponse } from "@/lib/api-types";

// ─── Internal server-only types (never sent to the client) ────────────────────

/**
 * Raw response from GitHub's device code endpoint.
 * @internal
 */
export interface GitHubDeviceCodeResponse {
  device_code: string;
  user_code: string;
  verification_uri: string;
  expires_in: number;
  interval: number;
}

/**
 * Raw response from GitHub's access token endpoint during polling.
 * @internal
 */
export interface GitHubAccessTokenResponse {
  access_token?: string;
  error?: string;
  /** Updated interval returned by GitHub on slow_down errors */
  interval?: number;
}
