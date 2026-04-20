import {
  computed,
  readonly,
  shallowRef,
  toValue,
  watch,
  type MaybeRefOrGetter,
  type ShallowRef,
} from "vue";
import { apiFetch } from "@/lib/api-client";
import type { GitHubPullRequest } from "./github-types";

const PER_PAGE = 30;

export interface GitHubPullsFilter {
  state?: "open" | "closed" | "all";
  sort?: "created" | "updated" | "popularity" | "long-running";
  direction?: "asc" | "desc";
}

export interface UseGitHubPullsOptions {
  owner: MaybeRefOrGetter<string | null>;
  repo: MaybeRefOrGetter<string | null>;
  filter?: MaybeRefOrGetter<GitHubPullsFilter | undefined>;
}

export interface UseGitHubPullsResult {
  pulls: Readonly<ShallowRef<readonly GitHubPullRequest[]>>;
  isLoading: Readonly<ShallowRef<boolean>>;
  error: Readonly<ShallowRef<string | null>>;
  hasMore: Readonly<ShallowRef<boolean>>;
  page: Readonly<ShallowRef<number>>;
  loadMore: () => void;
  refetch: () => void;
}

async function readErrorMessage(response: Response, fallback: string): Promise<string> {
  const payload = (await response.json().catch(() => ({}))) as { error?: string; message?: string };
  return payload.error ?? payload.message ?? fallback;
}

export function useGitHubPulls(options: UseGitHubPullsOptions): UseGitHubPullsResult {
  const pulls = shallowRef<GitHubPullRequest[]>([]);
  const isLoading = shallowRef(false);
  const error = shallowRef<string | null>(null);
  const hasMore = shallowRef(true);
  const page = shallowRef(1);
  const fetchKey = shallowRef(0);

  const owner = computed(() => toValue(options.owner));
  const repo = computed(() => toValue(options.repo));
  const filter = computed(() => {
    const source = toValue(options.filter);
    return {
      state: source?.state ?? "open",
      sort: source?.sort ?? "updated",
      direction: source?.direction ?? "desc",
    };
  });
  const filterKey = computed(() => JSON.stringify(filter.value));

  let requestId = 0;

  function resetState(): void {
    pulls.value = [];
    page.value = 1;
    hasMore.value = true;
    error.value = null;
  }

  watch([owner, repo, filterKey], resetState, { immediate: true });

  watch(
    [owner, repo, filter, page, fetchKey],
    async ([currentOwner, currentRepo, currentFilter, currentPage]) => {
      if (!currentOwner || !currentRepo) {
        pulls.value = [];
        hasMore.value = false;
        isLoading.value = false;
        return;
      }

      const currentRequestId = ++requestId;
      isLoading.value = true;
      error.value = null;

      try {
        const params = new URLSearchParams({
          state: currentFilter.state,
          sort: currentFilter.sort,
          direction: currentFilter.direction,
          page: currentPage.toString(),
          per_page: PER_PAGE.toString(),
        });

        const response = await apiFetch(
          `/api/integrations/github/repos/${currentOwner}/${currentRepo}/pulls?${params.toString()}`,
        );
        if (!response.ok) {
          throw new Error(await readErrorMessage(response, "Failed to load pull requests."));
        }

        const payload = (await response.json()) as GitHubPullRequest[];
        if (currentRequestId !== requestId) {
          return;
        }

        pulls.value = currentPage === 1 ? payload : [...pulls.value, ...payload];
        hasMore.value = payload.length === PER_PAGE;
      } catch (fetchError) {
        if (currentRequestId !== requestId) {
          return;
        }

        error.value = fetchError instanceof Error ? fetchError.message : "Failed to load pull requests.";
      } finally {
        if (currentRequestId === requestId) {
          isLoading.value = false;
        }
      }
    },
    { immediate: true },
  );

  return {
    pulls: readonly(pulls),
    isLoading: readonly(isLoading),
    error: readonly(error),
    hasMore: readonly(hasMore),
    page: readonly(page),
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
