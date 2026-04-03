"use client";

import { createRepoMetadataCache, type RepoMetadataCacheResult } from "./create-repo-metadata-cache";
import type { GitHubMilestone } from "../types";

const milestonesCache = createRepoMetadataCache<GitHubMilestone>({
  endpoint: (owner, repo) =>
    `/api/integrations/github/repos/${owner}/${repo}/milestones`,
  name: "milestones",
});

export function useGitHubMilestones(
  owner: string | null,
  repo: string | null
): RepoMetadataCacheResult<GitHubMilestone> {
  return milestonesCache.useRepoMetadata(owner, repo);
}
