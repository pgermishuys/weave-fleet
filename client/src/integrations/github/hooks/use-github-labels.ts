"use client";

import { createRepoMetadataCache, type RepoMetadataCacheResult } from "./create-repo-metadata-cache";
import type { GitHubLabel } from "../types";

const labelsCache = createRepoMetadataCache<GitHubLabel>({
  endpoint: (owner, repo) =>
    `/api/integrations/github/repos/${owner}/${repo}/labels`,
  name: "labels",
});

export function useGitHubLabels(
  owner: string | null,
  repo: string | null
): RepoMetadataCacheResult<GitHubLabel> {
  return labelsCache.useRepoMetadata(owner, repo);
}
