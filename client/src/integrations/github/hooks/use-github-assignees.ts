"use client";

import { createRepoMetadataCache, type RepoMetadataCacheResult } from "./create-repo-metadata-cache";
import type { GitHubAssignee } from "../types";

const assigneesCache = createRepoMetadataCache<GitHubAssignee>({
  endpoint: (owner, repo) =>
    `/api/integrations/github/repos/${owner}/${repo}/assignees`,
  name: "assignees",
});

export function useGitHubAssignees(
  owner: string | null,
  repo: string | null
): RepoMetadataCacheResult<GitHubAssignee> {
  return assigneesCache.useRepoMetadata(owner, repo);
}
