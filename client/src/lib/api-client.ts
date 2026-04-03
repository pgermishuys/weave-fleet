/**
 * API client — configurable base URL for all frontend API calls.
 *
 * Priority order for API base URL:
 *   1. Runtime override via `setApiBase(url)` (highest priority)
 *   2. `window.__WEAVE_API_BASE__` — injected by backend via <script> tag
 *   3. Build-time `NEXT_PUBLIC_API_BASE_URL` env var
 *   4. Empty string — relative URLs / same-origin mode (default)
 */

// Runtime-configurable base URL (overrides all other sources when set)
let runtimeBase: string | null = null;

/**
 * Override the API base URL at runtime.
 * Useful for multi-backend scenarios or testing.
 */
export function setApiBase(url: string): void {
  runtimeBase = url.replace(/\/$/, "");
}

function getApiBase(): string {
  if (runtimeBase !== null) return runtimeBase;
  // Check window global (can be injected by backend via <script> tag)
  if (typeof window !== "undefined" && (window as { __WEAVE_API_BASE__?: string }).__WEAVE_API_BASE__) {
    return ((window as { __WEAVE_API_BASE__?: string }).__WEAVE_API_BASE__ as string).replace(/\/$/, "");
  }
  // Fallback to build-time env var
  return (process.env.NEXT_PUBLIC_API_BASE_URL ?? "").replace(/\/$/, "");
}

/**
 * Build a full API URL from a path.
 * @param path - Must start with "/" (e.g. "/api/sessions")
 */
export function apiUrl(path: string): string {
  const base = getApiBase();
  return base ? `${base}${path}` : path;
}

/**
 * Build a full SSE URL from a path. Semantically identical to `apiUrl`
 * but named distinctly for readability at EventSource call sites.
 */
export const sseUrl = apiUrl;

/**
 * Build a WebSocket URL from an HTTP path.
 *
 * Converts the base URL scheme:
 *   http://host/path  → ws://host/path
 *   https://host/path → wss://host/path
 *
 * When no base URL is set (standalone / same-origin mode), derives the
 * WebSocket URL from the current window location at runtime.
 */
export function wsUrl(path: string): string {
  const base = getApiBase();
  if (base) {
    const wsBase = base.replace(/^http(s?):\/\//, (_, s: string) => `ws${s}://`);
    return `${wsBase}${path}`;
  }
  // Relative path — derive from window.location at runtime (SSR-safe)
  if (typeof window === "undefined") return path;
  const protocol = window.location.protocol === "https:" ? "wss:" : "ws:";
  return `${protocol}//${window.location.host}${path}`;
}

/**
 * Thin wrapper around `fetch()` that prepends the API base URL.
 * Drop-in replacement: `fetch("/api/foo")` → `apiFetch("/api/foo")`.
 */
export function apiFetch(
  path: string,
  init?: RequestInit
): Promise<Response> {
  return fetch(apiUrl(path), init);
}
