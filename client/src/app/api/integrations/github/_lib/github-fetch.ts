import { getIntegrationConfig } from "@/lib/server/integration-store";
import { log } from "@/lib/server/logger";

export interface GitHubFetchOptions {
  params?: Record<string, string | number | undefined>;
}

export interface GitHubFetchResult<T> {
  data?: T;
  error?: string;
  status: number;
  rateLimitRemaining?: number;
  rateLimitReset?: number;
}

/**
 * Returns the GitHub token from the integration store, or null if not configured.
 */
export function getGitHubToken(): string | null {
  const config = getIntegrationConfig("github");
  if (!config?.token || typeof config.token !== "string") {
    return null;
  }
  return config.token;
}

/**
 * Makes a request to the GitHub REST API with the stored token.
 * Handles auth, error mapping, and rate limit headers.
 */
export async function githubFetch<T>(
  path: string,
  token: string,
  options?: GitHubFetchOptions
): Promise<GitHubFetchResult<T>> {
  const url = new URL(`https://api.github.com${path}`);

  if (options?.params) {
    for (const [key, value] of Object.entries(options.params)) {
      if (value !== undefined) {
        url.searchParams.set(key, String(value));
      }
    }
  }

  let response: Response;
  try {
    response = await fetch(url.toString(), {
      headers: {
        Authorization: `Bearer ${token}`,
        Accept: "application/vnd.github+json",
        "X-GitHub-Api-Version": "2022-11-28",
        "User-Agent": "weave-agent-fleet",
      },
    });
  } catch (err) {
    log.warn("github-fetch", "Network error calling GitHub API", {
      path,
      err,
    });
    return { error: "Network error", status: 502 };
  }

  const rateLimitRemaining = response.headers.get("X-RateLimit-Remaining");
  const rateLimitReset = response.headers.get("X-RateLimit-Reset");

  if (!response.ok) {
    let errorMessage = `GitHub API error: ${response.status}`;
    try {
      const errorBody = (await response.json()) as { message?: string };
      if (errorBody.message) {
        errorMessage = errorBody.message;
      }
    } catch {
      // ignore JSON parse error
    }
    return {
      error: errorMessage,
      status: response.status,
      rateLimitRemaining: rateLimitRemaining
        ? parseInt(rateLimitRemaining, 10)
        : undefined,
      rateLimitReset: rateLimitReset ? parseInt(rateLimitReset, 10) : undefined,
    };
  }

  let data: T;
  try {
    data = (await response.json()) as T;
  } catch (err) {
    log.warn("github-fetch", "Failed to parse GitHub API response", {
      path,
      err,
    });
    return { error: "Invalid response from GitHub", status: 502 };
  }

  return {
    data,
    status: response.status,
    rateLimitRemaining: rateLimitRemaining
      ? parseInt(rateLimitRemaining, 10)
      : undefined,
    rateLimitReset: rateLimitReset ? parseInt(rateLimitReset, 10) : undefined,
  };
}
