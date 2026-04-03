"use client";

import { useState, useCallback } from "react";
import { apiFetch } from "@/lib/api-client";
import type { GitHubComment } from "../types";

interface UseGitHubCommentsResult {
  comments: GitHubComment[];
  isLoading: boolean;
  error: string | null;
  fetch: () => void;
}

export function useGitHubComments(
  owner: string,
  repo: string,
  type: "issues" | "pulls",
  number: number
): UseGitHubCommentsResult {
  const [comments, setComments] = useState<GitHubComment[]>([]);
  const [isLoading, setIsLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);

  const fetch = useCallback(() => {
    setIsLoading(true);
    setError(null);

    apiFetch(
      `/api/integrations/github/repos/${owner}/${repo}/${type}/${number}/comments`
    )
      .then((res) => res.json())
      .then((data: GitHubComment[]) => {
        setComments(data);
      })
      .catch((err: unknown) => {
        setError(err instanceof Error ? err.message : "Failed to load comments");
      })
      .finally(() => {
        setIsLoading(false);
      });
  }, [owner, repo, type, number]);

  return { comments, isLoading, error, fetch };
}
