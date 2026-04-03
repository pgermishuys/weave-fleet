// @vitest-environment jsdom

import { act, renderHook, waitFor } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";

const apiFetchMock = vi.fn();

vi.mock("@/lib/api-client", () => ({
  apiFetch: (...args: unknown[]) => apiFetchMock(...args),
}));

function mockResponse(data: unknown) {
  return {
    ok: true,
    json: async () => data,
  };
}

async function loadUseGitHubRepos() {
  const mod = await import("@/integrations/github/hooks/use-github-repos");
  return mod.useGitHubRepos;
}

describe("useGitHubRepos", () => {
  beforeEach(() => {
    vi.resetModules();
    apiFetchMock.mockReset();
    localStorage.clear();
  });

  it("clears legacy localStorage cache keys on mount", async () => {
    localStorage.setItem("weave:github:repos-cache", "[]");
    localStorage.setItem("weave:github:repos-cache-ts", "123");

    const useGitHubRepos = await loadUseGitHubRepos();
    renderHook(() => useGitHubRepos());

    expect(localStorage.getItem("weave:github:repos-cache")).toBeNull();
    expect(localStorage.getItem("weave:github:repos-cache-ts")).toBeNull();
  });

  it("loads repos into in-memory cache without persisting inventory", async () => {
    apiFetchMock.mockResolvedValueOnce(
      mockResponse([
        {
          id: 1,
          full_name: "octocat/hello-world",
          name: "hello-world",
          owner: { login: "octocat", avatar_url: "https://example.com" },
          private: true,
          language: "TypeScript",
          stargazers_count: 42,
        },
      ])
    );

    const useGitHubRepos = await loadUseGitHubRepos();
    const { result } = renderHook(() => useGitHubRepos());

    await act(async () => {
      result.current.refresh();
    });

    await waitFor(() => {
      expect(result.current.repos).toHaveLength(1);
      expect(result.current.repos[0]?.owner_login).toBe("octocat");
    });

    expect(localStorage.getItem("weave:github:repos-cache")).toBeNull();
    expect(localStorage.getItem("weave:github:repos-cache-ts")).toBeNull();
  });

  it("deduplicates concurrent refresh calls", async () => {
    apiFetchMock.mockResolvedValue(mockResponse([]));

    const useGitHubRepos = await loadUseGitHubRepos();
    const { result } = renderHook(() => useGitHubRepos());

    act(() => {
      result.current.refresh();
      result.current.refresh();
    });

    await waitFor(() => {
      expect(apiFetchMock).toHaveBeenCalledTimes(1);
    });
  });

  it("ignores in-flight fetch results after clear", async () => {
    let resolveResponse: ((value: unknown) => void) | null = null;
    apiFetchMock.mockReturnValue(
      new Promise((resolve) => {
        resolveResponse = resolve;
      })
    );

    const useGitHubRepos = await loadUseGitHubRepos();
    const { result } = renderHook(() => useGitHubRepos());

    act(() => {
      result.current.refresh();
    });

    act(() => {
      result.current.clear();
    });

    await act(async () => {
      resolveResponse?.(
        mockResponse([
          {
            id: 1,
            full_name: "octocat/hello-world",
            name: "hello-world",
            owner: { login: "octocat", avatar_url: "https://example.com" },
            private: true,
            language: "TypeScript",
            stargazers_count: 42,
          },
        ])
      );
      await Promise.resolve();
    });

    await waitFor(() => {
      expect(result.current.repos).toEqual([]);
      expect(result.current.lastUpdated).toBeNull();
      expect(result.current.isLoading).toBe(false);
    });
  });
});
