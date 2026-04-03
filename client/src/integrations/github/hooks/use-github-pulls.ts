"use client";

import { useState, useEffect, useCallback, useRef } from "react";
import { apiFetch } from "@/lib/api-client";
import type { GitHubPullRequest } from "../types";

interface UseGitHubPullsOptions {
  state?: "open" | "closed" | "all";
  sort?: "created" | "updated" | "popularity" | "long-running";
  direction?: "asc" | "desc";
}

interface UseGitHubPullsResult {
  pulls: GitHubPullRequest[];
  isLoading: boolean;
  error: string | null;
  hasMore: boolean;
  loadMore: () => void;
  refetch: () => void;
}

export function useGitHubPulls(
  owner: string | null,
  repo: string | null,
  options: UseGitHubPullsOptions = {}
): UseGitHubPullsResult {
  const { state = "open", sort = "updated", direction = "desc" } = options;
  const [pulls, setPulls] = useState<GitHubPullRequest[]>([]);
  const [isLoading, setIsLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [page, setPage] = useState(1);
  const [hasMore, setHasMore] = useState(true);
  const [fetchKey, setFetchKey] = useState(0);

  const PER_PAGE = 30;

  // Reset state when owner/repo changes (including to null)
  const ownerRepoKey = owner && repo ? `${owner}/${repo}` : null;
  const prevKeyRef = useRef(ownerRepoKey);
  useEffect(() => {
    if (prevKeyRef.current !== ownerRepoKey) {
      prevKeyRef.current = ownerRepoKey;
      setPulls([]);
      setPage(1);
      setHasMore(true);
      setError(null);
    }
  }, [ownerRepoKey]);

  useEffect(() => {
    if (!owner || !repo) return;

    let cancelled = false;

    const fetchPulls = async () => {
      setIsLoading(true);
      setError(null);

      const params = new URLSearchParams({
        state,
        sort,
        direction,
        page: String(page),
        per_page: String(PER_PAGE),
      });

      try {
        const res = await apiFetch(`/api/integrations/github/repos/${owner}/${repo}/pulls?${params}`);
        const data: GitHubPullRequest[] = await res.json();
        if (cancelled) return;
        if (page === 1) {
          setPulls(data);
        } else {
          setPulls((prev) => [...prev, ...data]);
        }
        setHasMore(data.length === PER_PAGE);
      } catch (err: unknown) {
        if (cancelled) return;
        setError(err instanceof Error ? err.message : "Failed to load pull requests");
      } finally {
        if (!cancelled) setIsLoading(false);
      }
    };

    fetchPulls();

    return () => {
      cancelled = true;
    };
  }, [owner, repo, state, sort, direction, page, fetchKey]);

  const loadMore = useCallback(() => {
    if (!isLoading && hasMore) {
      setPage((p) => p + 1);
    }
  }, [isLoading, hasMore]);

  const refetch = useCallback(() => {
    setPage(1);
    setPulls([]);
    setFetchKey((k) => k + 1);
  }, []);

  return { pulls, isLoading, error, hasMore, loadMore, refetch };
}
