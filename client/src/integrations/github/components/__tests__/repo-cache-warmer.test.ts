// @vitest-environment jsdom

import React from "react";
import { render } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { GitHubRepoCacheWarmer } from "@/integrations/github/components/repo-cache-warmer";

const {
  refreshMock,
  clearMock,
  clearGitHubClientStateMock,
  state,
} = vi.hoisted(() => ({
  refreshMock: vi.fn(),
  clearMock: vi.fn(),
  clearGitHubClientStateMock: vi.fn(),
  state: {
    connectedIntegrations: [] as Array<{ id: string }>,
    reposLength: 0,
    isStale: false,
  },
}));

vi.mock("@/contexts/integrations-context", () => ({
  useIntegrationsContext: () => ({
    connectedIntegrations: state.connectedIntegrations,
  }),
}));

vi.mock("@/integrations/github/hooks/use-github-repos", () => ({
  useGitHubRepos: () => ({
    repos: Array.from({ length: state.reposLength }, (_, idx) => ({ id: idx })),
    isStale: state.isStale,
    refresh: refreshMock,
    clear: clearMock,
  }),
}));

vi.mock("@/integrations/github/storage", () => ({
  clearGitHubClientState: clearGitHubClientStateMock,
}));

describe("GitHubRepoCacheWarmer", () => {
  beforeEach(() => {
    state.connectedIntegrations = [];
    state.reposLength = 0;
    state.isStale = false;
    refreshMock.mockReset();
    clearMock.mockReset();
    clearGitHubClientStateMock.mockReset();
  });

  it("warms cache when connected and empty", () => {
    state.connectedIntegrations = [{ id: "github" }];
    state.reposLength = 0;

    render(React.createElement(GitHubRepoCacheWarmer));

    expect(refreshMock).toHaveBeenCalledTimes(1);
    expect(clearMock).not.toHaveBeenCalled();
  });

  it("warms cache when connected and stale", () => {
    state.connectedIntegrations = [{ id: "github" }];
    state.reposLength = 2;
    state.isStale = true;

    render(React.createElement(GitHubRepoCacheWarmer));

    expect(refreshMock).toHaveBeenCalledTimes(1);
  });

  it("clears all GitHub client state when disconnected", () => {
    state.connectedIntegrations = [];

    render(React.createElement(GitHubRepoCacheWarmer));

    expect(clearGitHubClientStateMock).toHaveBeenCalledTimes(1);
    expect(clearMock).toHaveBeenCalledTimes(1);
    expect(refreshMock).not.toHaveBeenCalled();
  });
});
