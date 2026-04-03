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

async function loadUseGitHubMilestones() {
  const mod = await import("@/integrations/github/hooks/use-github-milestones");
  return mod.useGitHubMilestones;
}

describe("useGitHubMilestones", () => {
  beforeEach(() => {
    vi.resetModules();
    apiFetchMock.mockReset();
  });

  it("fetches milestones on mount", async () => {
    const milestones = [
      { number: 1, title: "v1.0", state: "open", open_issues: 5, closed_issues: 10 },
    ];
    apiFetchMock.mockResolvedValueOnce(mockResponse(milestones));

    const useGitHubMilestones = await loadUseGitHubMilestones();
    const { result } = renderHook(() => useGitHubMilestones("acme", "my-project"));

    await waitFor(() => {
      expect(result.current.data).toHaveLength(1);
      expect(result.current.data[0]?.title).toBe("v1.0");
    });

    expect(apiFetchMock).toHaveBeenCalledWith(
      "/api/integrations/github/repos/acme/my-project/milestones"
    );
  });

  it("returns empty data when owner/repo are null", async () => {
    const useGitHubMilestones = await loadUseGitHubMilestones();
    const { result } = renderHook(() => useGitHubMilestones(null, null));

    expect(result.current.data).toEqual([]);
    expect(apiFetchMock).not.toHaveBeenCalled();
  });
});
