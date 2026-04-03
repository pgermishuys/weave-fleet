"use client";

import { useEffect } from "react";
import { useIntegrationsContext } from "@/contexts/integrations-context";
import { useGitHubRepos } from "../hooks/use-github-repos";
import { clearGitHubClientState } from "../storage";

/**
 * Invisible component that keeps the repo cache warm.
 * - On startup: if GitHub is connected and cache is empty or stale, preloads all repos.
 * - On disconnect: clears the cache.
 * Mount at app-layout level so it's always active.
 */
export function GitHubRepoCacheWarmer() {
  const { connectedIntegrations } = useIntegrationsContext();
  const { repos, isStale, refresh, clear } = useGitHubRepos();

  const isGitHubConnected = connectedIntegrations.some(
    (i) => i.id === "github"
  );

  useEffect(() => {
    if (isGitHubConnected && (repos.length === 0 || isStale)) {
      refresh();
    }
    if (!isGitHubConnected) {
      clearGitHubClientState();
      clear();
    }
  }, [isGitHubConnected, repos.length, isStale, refresh, clear]);

  return null;
}
