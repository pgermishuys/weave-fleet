// @vitest-environment jsdom

import { renderHook, waitFor } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";

const apiFetchMock = vi.fn();

vi.mock("@/lib/api-client", () => ({
  apiFetch: (...args: unknown[]) => apiFetchMock(...args),
}));

function mockResponse(data: unknown) {
  return { ok: true, json: async () => data };
}

async function loadUseGitHubAssignees() {
  const mod = await import("@/integrations/github/hooks/use-github-assignees");
  return mod.useGitHubAssignees;
}

describe("useGitHubAssignees", () => {
  beforeEach(() => {
    vi.resetModules();
    apiFetchMock.mockReset();
  });

  it("fetches assignees on mount", async () => {
    const assignees = [
      { login: "octocat", avatar_url: "https://example.com/avatar" },
    ];
    apiFetchMock.mockResolvedValueOnce(mockResponse(assignees));

    const useGitHubAssignees = await loadUseGitHubAssignees();
    const { result } = renderHook(() => useGitHubAssignees("acme", "my-project"));

    await waitFor(() => {
      expect(result.current.data).toHaveLength(1);
      expect(result.current.data[0]?.login).toBe("octocat");
    });

    expect(apiFetchMock).toHaveBeenCalledWith(
      "/api/integrations/github/repos/acme/my-project/assignees"
    );
  });

  it("returns empty data when owner/repo are null", async () => {
    const useGitHubAssignees = await loadUseGitHubAssignees();
    const { result } = renderHook(() => useGitHubAssignees(null, null));

    expect(result.current.data).toEqual([]);
    expect(apiFetchMock).not.toHaveBeenCalled();
  });
});
