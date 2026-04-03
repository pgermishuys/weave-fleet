"use client";

import { useState, useEffect, useCallback, useRef } from "react";
import { apiFetch } from "@/lib/api-client";
import {
  DEFAULT_ISSUE_FILTER,
  type GitHubIssue,
  type IssueFilterState,
  type GitHubMilestone,
} from "../types";

interface SearchApiResponse {
  total_count: number;
  incomplete_results: boolean;
  items: GitHubIssue[];
}

interface UseGitHubIssuesResult {
  issues: GitHubIssue[];
  isLoading: boolean;
  isSearching: boolean;
  error: string | null;
  hasMore: boolean;
  loadMore: () => void;
  refetch: () => void;
}

const PER_PAGE = 30;
const SEARCH_DEBOUNCE_MS = 300;

/**
 * Hook to fetch GitHub issues with full filter state support.
 *
 * Uses the REST issues endpoint when no search text is present, and
 * switches to the Search API when search is non-empty (with debounce).
 *
 * @param milestones — optional cached milestones for title→number mapping
 */
export function useGitHubIssues(
  owner: string | null,
  repo: string | null,
  filter: IssueFilterState = DEFAULT_ISSUE_FILTER,
  milestones?: GitHubMilestone[]
): UseGitHubIssuesResult {
  const [issues, setIssues] = useState<GitHubIssue[]>([]);
  const [isLoading, setIsLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [page, setPage] = useState(1);
  const [hasMore, setHasMore] = useState(true);
  const [fetchKey, setFetchKey] = useState(0);

  // Debounced search value
  const [debouncedSearch, setDebouncedSearch] = useState(filter.search);
  const isSearching = filter.search !== debouncedSearch;

  useEffect(() => {
    if (!filter.search) {
      setDebouncedSearch("");
      return;
    }
    const timer = setTimeout(() => {
      setDebouncedSearch(filter.search);
    }, SEARCH_DEBOUNCE_MS);
    return () => clearTimeout(timer);
  }, [filter.search]);

  // Serialise the non-search filter params for dependency tracking
  const filterKey = JSON.stringify({
    state: filter.state,
    labels: filter.labels,
    milestone: filter.milestone,
    assignee: filter.assignee,
    author: filter.author,
    type: filter.type,
    sort: filter.sort,
    direction: filter.direction,
  });

  // Reset pagination when filters or owner/repo change
  const ownerRepoKey = owner && repo ? `${owner}/${repo}` : null;
  const prevKeyRef = useRef<string | null>(null);
  const prevFilterKeyRef = useRef(filterKey);
  const prevSearchRef = useRef(debouncedSearch);

  useEffect(() => {
    const changed =
      prevKeyRef.current !== ownerRepoKey ||
      prevFilterKeyRef.current !== filterKey ||
      prevSearchRef.current !== debouncedSearch;

    prevKeyRef.current = ownerRepoKey;
    prevFilterKeyRef.current = filterKey;
    prevSearchRef.current = debouncedSearch;

    if (changed) {
      setIssues([]);
      setPage(1);
      setHasMore(true);
      setError(null);
    }
  }, [ownerRepoKey, filterKey, debouncedSearch]);

  // Resolve milestone title → number using the cached milestones list
  const resolveMilestoneNumber = useCallback(
    (title: string | null): string | undefined => {
      if (!title) return undefined;
      // Special values pass through as-is
      if (title === "*" || title === "none") return title;
      // Look up the milestone number from cached milestones
      if (milestones) {
        const ms = milestones.find((m) => m.title === title);
        if (ms) return String(ms.number);
      }
      // If not found, skip this filter (don't send an invalid milestone number)
      return undefined;
    },
    [milestones]
  );

  // Main fetch effect
  useEffect(() => {
    if (!owner || !repo) return;

    let cancelled = false;

    const fetchIssues = async () => {
      setIsLoading(true);
      setError(null);

      try {
        let data: GitHubIssue[];
        let totalCount: number | null = null;

        if (debouncedSearch) {
          // ─── Search API mode ─────────────────────────────────────
          // Build qualifier-based query for the search endpoint
          const qualifiers: string[] = [];

          if (filter.state !== "all") {
            qualifiers.push(`is:${filter.state}`);
          }
          for (const label of filter.labels) {
            qualifiers.push(`label:"${label}"`);
          }
          if (filter.author) {
            qualifiers.push(`author:${filter.author}`);
          }
          if (filter.assignee) {
            if (filter.assignee === "none") qualifiers.push("no:assignee");
            else if (filter.assignee !== "*") qualifiers.push(`assignee:${filter.assignee}`);
          }
          const msNumber = resolveMilestoneNumber(filter.milestone);
          if (filter.milestone && msNumber) {
            qualifiers.push(`milestone:"${filter.milestone}"`);
          }

          const q = [...qualifiers, debouncedSearch].join(" ");
          const params = new URLSearchParams({
            q,
            page: String(page),
            per_page: String(PER_PAGE),
          });

          // Note: sort via search API has different semantics;
          // we pass sort + order as query params
          if (filter.sort !== DEFAULT_ISSUE_FILTER.sort || filter.direction !== DEFAULT_ISSUE_FILTER.direction) {
            // Search API uses "sort" and "order" (not "direction")
            const searchSort = filter.sort === "comments" ? "comments" : filter.sort;
            params.set("sort", searchSort);
            params.set("order", filter.direction);
          }

          const res = await apiFetch(
            `/api/integrations/github/repos/${owner}/${repo}/issues/search?${params}`
          );
          if (!res.ok) {
            const errBody = await res.json().catch(() => ({}));
            throw new Error(
              (errBody as { error?: string }).error ?? "Search failed"
            );
          }
          const searchResult: SearchApiResponse = await res.json();
          data = searchResult.items;
          totalCount = searchResult.total_count;
        } else {
          // ─── REST API mode ───────────────────────────────────────
          const params = new URLSearchParams({
            state: filter.state,
            sort: filter.sort,
            direction: filter.direction,
            page: String(page),
            per_page: String(PER_PAGE),
          });

          // Optional filters — only include when set
          if (filter.labels.length > 0) {
            params.set("labels", filter.labels.join(","));
          }
          const msNumber = resolveMilestoneNumber(filter.milestone);
          if (msNumber) {
            params.set("milestone", msNumber);
          }
          if (filter.assignee) {
            params.set("assignee", filter.assignee);
          }
          if (filter.author) {
            params.set("creator", filter.author);
          }
          if (filter.type) {
            params.set("type", filter.type);
          }

          const res = await apiFetch(
            `/api/integrations/github/repos/${owner}/${repo}/issues?${params}`
          );
          if (!res.ok) {
            const errBody = await res.json().catch(() => ({}));
            throw new Error(
              (errBody as { error?: string }).error ?? "Failed to load issues"
            );
          }
          data = await res.json();
        }

        if (cancelled) return;

        // Filter out pull requests (GitHub issues endpoint also returns PRs)
        const issuesOnly = data.filter((i) => !i.pull_request);

        if (page === 1) {
          setIssues(issuesOnly);
        } else {
          setIssues((prev) => [...prev, ...issuesOnly]);
        }

        // Determine hasMore
        if (totalCount !== null) {
          // Search mode: compare accumulated count vs total
          const accumulated = page === 1 ? issuesOnly.length : issues.length + issuesOnly.length;
          setHasMore(accumulated < totalCount);
        } else {
          // REST mode: full page means more data might exist
          setHasMore(data.length === PER_PAGE);
        }
      } catch (err: unknown) {
        if (cancelled) return;
        setError(err instanceof Error ? err.message : "Failed to load issues");
      } finally {
        if (!cancelled) setIsLoading(false);
      }
    };

    fetchIssues();

    return () => {
      cancelled = true;
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [owner, repo, filterKey, debouncedSearch, page, fetchKey]);

  const loadMore = useCallback(() => {
    if (!isLoading && hasMore) {
      setPage((p) => p + 1);
    }
  }, [isLoading, hasMore]);

  const refetch = useCallback(() => {
    setPage(1);
    setIssues([]);
    setFetchKey((k) => k + 1);
  }, []);

  return { issues, isLoading, isSearching, error, hasMore, loadMore, refetch };
}
