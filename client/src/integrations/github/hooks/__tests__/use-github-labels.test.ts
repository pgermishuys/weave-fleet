// @vitest-environment jsdom

import { act, renderHook, waitFor } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";

const apiFetchMock = vi.fn();

vi.mock("@/lib/api-client", () => ({
  apiFetch: (...args: unknown[]) => apiFetchMock(...args),
}));

function mockResponse(data: unknown) {
  return { ok: true, json: async () => data };
}

function mockErrorResponse() {
  return { ok: false, json: async () => ({}) };
}

async function loadUseGitHubLabels() {
  const mod = await import("@/integrations/github/hooks/use-github-labels");
  return mod.useGitHubLabels;
}

describe("useGitHubLabels", () => {
  beforeEach(() => {
    vi.resetModules();
    apiFetchMock.mockReset();
  });

  it("fetches labels on mount when owner/repo are provided", async () => {
    const labels = [
      { name: "bug", color: "d73a4a", description: "Something broken" },
      { name: "enhancement", color: "a2eeef", description: null },
    ];
    apiFetchMock.mockResolvedValueOnce(mockResponse(labels));

    const useGitHubLabels = await loadUseGitHubLabels();
    const { result } = renderHook(() => useGitHubLabels("acme", "my-project"));

    await waitFor(() => {
      expect(result.current.data).toHaveLength(2);
      expect(result.current.data[0]?.name).toBe("bug");
      expect(result.current.isLoading).toBe(false);
    });

    expect(apiFetchMock).toHaveBeenCalledWith(
      "/api/integrations/github/repos/acme/my-project/labels"
    );
  });

  it("returns empty data when owner/repo are null", async () => {
    const useGitHubLabels = await loadUseGitHubLabels();
    const { result } = renderHook(() => useGitHubLabels(null, null));

    expect(result.current.data).toEqual([]);
    expect(result.current.isLoading).toBe(false);
    expect(apiFetchMock).not.toHaveBeenCalled();
  });

  it("handles error response", async () => {
    apiFetchMock.mockResolvedValueOnce(mockErrorResponse());

    const useGitHubLabels = await loadUseGitHubLabels();
    const { result } = renderHook(() => useGitHubLabels("acme", "my-project"));

    await waitFor(() => {
      expect(result.current.error).toBeTruthy();
      expect(result.current.isLoading).toBe(false);
    });
  });

  it("refresh forces a re-fetch", async () => {
    const labels = [{ name: "bug", color: "d73a4a", description: null }];
    apiFetchMock.mockResolvedValue(mockResponse(labels));

    const useGitHubLabels = await loadUseGitHubLabels();
    const { result } = renderHook(() => useGitHubLabels("acme", "my-project"));

    await waitFor(() => {
      expect(result.current.data).toHaveLength(1);
    });

    // Force refresh
    await act(async () => {
      result.current.refresh();
    });

    await waitFor(() => {
      // Should have been called twice: initial mount + refresh
      expect(apiFetchMock).toHaveBeenCalledTimes(2);
    });
  });

  it("deduplicates concurrent fetches for the same repo", async () => {
    let resolveResponse: ((value: unknown) => void) | null = null;
    apiFetchMock.mockReturnValue(
      new Promise((resolve) => {
        resolveResponse = resolve;
      })
    );

    const useGitHubLabels = await loadUseGitHubLabels();
    const { result } = renderHook(() => useGitHubLabels("acme", "my-project"));

    // Trigger another refresh while first is in-flight
    act(() => {
      result.current.refresh();
    });

    await act(async () => {
      resolveResponse?.(mockResponse([{ name: "bug", color: "d73a4a", description: null }]));
      await Promise.resolve();
    });

    await waitFor(() => {
      // Only one actual API call should have been made
      expect(apiFetchMock).toHaveBeenCalledTimes(1);
    });
  });
});
