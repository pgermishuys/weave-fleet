/**
 * GitHub OAuth App configuration for the Device Authorization flow (RFC 8628).
 *
 * The client ID is a public identifier — safe to hardcode, same approach
 * used by opencode (Ov23li8tweQw6odWQebz) and GitHub CLI.
 * It cannot be used to obtain tokens without user interaction.
 */

/** Public OAuth App client ID for Weave Agent Fleet */
export const GITHUB_OAUTH_CLIENT_ID = "Ov23liJT2Q0HXHj9xLGM";

/** GitHub device code initiation endpoint */
export const GITHUB_DEVICE_CODE_URL = "https://github.com/login/device/code";

/** GitHub access token polling endpoint */
export const GITHUB_ACCESS_TOKEN_URL =
  "https://github.com/login/oauth/access_token";

/**
 * OAuth scopes requested during device flow.
 * - `repo`: full access to repositories, issues, pull requests, and comments
 * - `read:user`: read user profile (to display username after connect)
 */
export const OAUTH_SCOPES = "repo read:user";

/**
 * Safety margin added to GitHub's polling interval to avoid clock skew / timer drift.
 * Same value used by opencode.
 */
export const OAUTH_POLLING_SAFETY_MARGIN_MS = 3000;
