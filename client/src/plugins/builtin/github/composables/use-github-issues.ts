import {
  computed,
  onUnmounted,
  readonly,
  shallowRef,
  toValue,
  watch,
  type ComputedRef,
  type MaybeRefOrGetter,
  type ShallowRef,
} from "vue";
import { apiFetch } from "@/lib/api-client";
import type { GitHubIssue, GitHubMilestone, IssueFilterState } from "./github-types";
import { DEFAULT_ISSUE_FILTER } from "./github-types";

interface SearchApiResponse {
  total_count: number;
  incomplete_results: boolean;
  items: GitHubIssue[];
}

export interface UseGitHubIssuesOptions {
  owner: MaybeRefOrGetter<string | null>;
  repo: MaybeRefOrGetter<string | null>;
  filter?: MaybeRefOrGetter<Partial<IssueFilterState> | undefined>;
  milestones?: MaybeRefOrGetter<readonly GitHubMilestone[] | undefined>;
}

export interface UseGitHubIssuesResult {
  issues: Readonly<ShallowRef<readonly GitHubIssue[]>>;
  isLoading: Readonly<ShallowRef<boolean>>;
  isSearching: Readonly<ComputedRef<boolean>>;
  error: Readonly<ShallowRef<string | null>>;
  hasMore: Readonly<ShallowRef<boolean>>;
  page: Readonly<ShallowRef<number>>;
  debouncedSearch: Readonly<ShallowRef<string>>;
  loadMore: () => void;
  refetch: () => void;
}

const PER_PAGE = 30;
const SEARCH_DEBOUNCE_MS = 300;

function mergeIssueFilter(filter?: Partial<IssueFilterState>): IssueFilterState {
  return {
    ...DEFAULT_ISSUE_FILTER,
    ...filter,
    labels: filter?.labels ? [...filter.labels] : [...DEFAULT_ISSUE_FILTER.labels],
    search: filter?.search ?? DEFAULT_ISSUE_FILTER.search,
  };
}

async function readErrorMessage(response: Response, fallback: string): Promise<string> {
  const payload = (await response.json().catch(() => ({}))) as { error?: string; message?: string };
  return payload.error ?? payload.message ?? fallback;
}

export function useGitHubIssues(options: UseGitHubIssuesOptions): UseGitHubIssuesResult {
  const issues = shallowRef<GitHubIssue[]>([]);
  const isLoading = shallowRef(false);
  const error = shallowRef<string | null>(null);
  const hasMore = shallowRef(true);
  const page = shallowRef(1);
  const fetchKey = shallowRef(0);
  const debouncedSearch = shallowRef("");

  const owner = computed(() => toValue(options.owner));
  const repo = computed(() => toValue(options.repo));
  const milestones = computed(() => toValue(options.milestones));
  const filter = computed(() => mergeIssueFilter(toValue(options.filter)));
  const filterKey = computed(() => JSON.stringify({
    state: filter.value.state,
    labels: filter.value.labels,
    milestone: filter.value.milestone,
    assignee: filter.value.assignee,
    author: filter.value.author,
    type: filter.value.type,
    sort: filter.value.sort,
    direction: filter.value.direction,
  }));
  const isSearching = computed(() => filter.value.search.trim() !== debouncedSearch.value);

  let debounceTimeoutId: ReturnType<typeof setTimeout> | undefined;
  let requestId = 0;

  function resolveMilestoneNumber(title: string | null): string | undefined {
    if (!title) {
      return undefined;
    }

    if (title === "*" || title === "none") {
      return title;
    }

    return milestones.value?.find((milestone) => milestone.title === title)?.number.toString();
  }

  function resetState(): void {
    issues.value = [];
    page.value = 1;
    hasMore.value = true;
    error.value = null;
  }

  watch(
    () => filter.value.search,
    (search) => {
      if (debounceTimeoutId) {
        clearTimeout(debounceTimeoutId);
        debounceTimeoutId = undefined;
      }

      const trimmedSearch = search.trim();
      if (!trimmedSearch) {
        debouncedSearch.value = "";
        return;
      }

      debounceTimeoutId = setTimeout(() => {
        debouncedSearch.value = trimmedSearch;
        debounceTimeoutId = undefined;
      }, SEARCH_DEBOUNCE_MS);
    },
    { immediate: true },
  );

  watch([owner, repo, filterKey, debouncedSearch], resetState, { immediate: true });

  watch(
    [owner, repo, filter, debouncedSearch, page, fetchKey],
    async ([currentOwner, currentRepo, currentFilter, currentSearch, currentPage]) => {
      if (!currentOwner || !currentRepo) {
        issues.value = [];
        hasMore.value = false;
        isLoading.value = false;
        return;
      }

      const currentRequestId = ++requestId;
      isLoading.value = true;
      error.value = null;

      try {
        let responseIssues: GitHubIssue[];
        let totalCount: number | null = null;

        if (currentSearch) {
          const qualifiers: string[] = [];

          if (currentFilter.state !== "all") {
            qualifiers.push(`is:${currentFilter.state}`);
          }

          for (const label of currentFilter.labels) {
            qualifiers.push(`label:"${label}"`);
          }

          if (currentFilter.author) {
            qualifiers.push(`author:${currentFilter.author}`);
          }

          if (currentFilter.assignee) {
            if (currentFilter.assignee === "none") {
              qualifiers.push("no:assignee");
            } else if (currentFilter.assignee !== "*") {
              qualifiers.push(`assignee:${currentFilter.assignee}`);
            }
          }

          const milestoneNumber = resolveMilestoneNumber(currentFilter.milestone);
          if (currentFilter.milestone && milestoneNumber) {
            qualifiers.push(`milestone:"${currentFilter.milestone}"`);
          }

          const params = new URLSearchParams({
            q: [...qualifiers, currentSearch].join(" "),
            page: currentPage.toString(),
            per_page: PER_PAGE.toString(),
          });

          if (
            currentFilter.sort !== DEFAULT_ISSUE_FILTER.sort
            || currentFilter.direction !== DEFAULT_ISSUE_FILTER.direction
          ) {
            params.set("sort", currentFilter.sort === "comments" ? "comments" : currentFilter.sort);
            params.set("order", currentFilter.direction);
          }

          const response = await apiFetch(
            `/api/integrations/github/repos/${currentOwner}/${currentRepo}/issues/search?${params.toString()}`,
          );
          if (!response.ok) {
            throw new Error(await readErrorMessage(response, "Search failed."));
          }

          const payload = (await response.json()) as SearchApiResponse;
          responseIssues = payload.items;
          totalCount = payload.total_count;
        } else {
          const params = new URLSearchParams({
            state: currentFilter.state,
            sort: currentFilter.sort,
            direction: currentFilter.direction,
            page: currentPage.toString(),
            per_page: PER_PAGE.toString(),
          });

          if (currentFilter.labels.length > 0) {
            params.set("labels", currentFilter.labels.join(","));
          }

          const milestoneNumber = resolveMilestoneNumber(currentFilter.milestone);
          if (milestoneNumber) {
            params.set("milestone", milestoneNumber);
          }

          if (currentFilter.assignee) {
            params.set("assignee", currentFilter.assignee);
          }

          if (currentFilter.author) {
            params.set("creator", currentFilter.author);
          }

          if (currentFilter.type) {
            params.set("type", currentFilter.type);
          }

          const response = await apiFetch(
            `/api/integrations/github/repos/${currentOwner}/${currentRepo}/issues?${params.toString()}`,
          );
          if (!response.ok) {
            throw new Error(await readErrorMessage(response, "Failed to load issues."));
          }

          responseIssues = (await response.json()) as GitHubIssue[];
        }

        if (currentRequestId !== requestId) {
          return;
        }

        const issuesOnly = responseIssues.filter((issue) => !issue.pull_request);
        issues.value = currentPage === 1 ? issuesOnly : [...issues.value, ...issuesOnly];
        hasMore.value = totalCount !== null
          ? currentPage * PER_PAGE < totalCount
          : responseIssues.length === PER_PAGE;
      } catch (fetchError) {
        if (currentRequestId !== requestId) {
          return;
        }

        error.value = fetchError instanceof Error ? fetchError.message : "Failed to load issues.";
      } finally {
        if (currentRequestId === requestId) {
          isLoading.value = false;
        }
      }
    },
    { immediate: true },
  );

  onUnmounted(() => {
    if (debounceTimeoutId) {
      clearTimeout(debounceTimeoutId);
      debounceTimeoutId = undefined;
    }
  });

  return {
    issues: readonly(issues),
    isLoading: readonly(isLoading),
    isSearching,
    error: readonly(error),
    hasMore: readonly(hasMore),
    page: readonly(page),
    debouncedSearch: readonly(debouncedSearch),
    loadMore: () => {
      if (!isLoading.value && hasMore.value) {
        page.value += 1;
      }
    },
    refetch: () => {
      requestId += 1;
      resetState();
      fetchKey.value += 1;
    },
  };
}
