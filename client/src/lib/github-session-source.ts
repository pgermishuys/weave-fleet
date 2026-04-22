import type { ScannedRepository, SessionSourceSelection } from "@/lib/api-types";

export type GitHubSessionSourceType = "github-issue" | "github-pull-request";
export type GitHubRepositoryIsolationStrategy = "existing" | "worktree" | "clone";

export interface GitHubSessionSourcePreset {
  kind: "github";
  sourceType: GitHubSessionSourceType;
  owner: string;
  repo: string;
  number: number;
  title: string;
  body: string | null;
  htmlUrl: string;
  repoFullName: string;
  suggestedBranch?: string | null;
}

export function createGitHubSessionSourcePreset(input: Omit<GitHubSessionSourcePreset, "kind">): GitHubSessionSourcePreset {
  return {
    kind: "github",
    ...input,
  };
}

export function buildGitHubSessionSourceSelection(
  preset: GitHubSessionSourcePreset,
  repositoryPath: string,
  isolationStrategy: GitHubRepositoryIsolationStrategy,
  branch?: string,
): SessionSourceSelection {
  return {
    key: {
      providerId: "builtin.github",
      sourceType: preset.sourceType,
      actionId: "start-session",
      contractVersion: 1,
    },
    input: {
      owner: preset.owner,
      repo: preset.repo,
      number: preset.number,
      repositoryPath,
      isolationStrategy,
      ...(branch ? { branch } : {}),
    },
  };
}

export function findRepositoryForGitHubPreset(
  preset: GitHubSessionSourcePreset,
  repositories: readonly ScannedRepository[],
): ScannedRepository | null {
  const exactNameMatch = repositories.find((repository) => repository.name === preset.repo);
  if (exactNameMatch) {
    return exactNameMatch;
  }

  const normalizedRepo = preset.repo.toLowerCase();
  return repositories.find((repository) => repository.name.toLowerCase() === normalizedRepo) ?? null;
}
