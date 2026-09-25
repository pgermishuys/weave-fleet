<script setup lang="ts">
import { computed, onMounted, shallowRef, watch } from "vue";
import { useRouter } from "@tanstack/vue-router";
import { CircleDot, ExternalLink, GitPullRequest, LoaderCircle, RefreshCw, Search, TriangleAlert } from "lucide-vue-next";
import type { SessionListItem } from "@/api/client";
import GitHubItemRow from "@/components/github/GitHubItemRow.vue";
import { useGitHubSearch } from "@/composables/use-github-search";
import { useGitHubSessions } from "@/composables/use-github-sessions";
import { useGitHubWork } from "@/composables/use-github-work";
import {
  fetchGitHubItemsByNumber,
  issueFilterQuery,
  itemResourceId,
  itemRoute,
  itemSessionPreset,
  type GitHubItemSummary,
} from "@/lib/github-items";
import IssueFilterBar from "@/plugins/builtin/github/components/IssueFilterBar.vue";
import {
  DEFAULT_ISSUE_FILTER,
  type GitHubAssignee,
  type GitHubLabel,
  type GitHubMilestone,
  type IssueFilterState,
} from "@/plugins/builtin/github/composables/github-types";
import { useGitHubAssignees, useGitHubLabels, useGitHubMilestones } from "@/plugins/builtin/github/composables/use-github-metadata";
import { isPullRequest } from "@/lib/smart-links";
import { useSmartLinksStore } from "@/stores/smart-links";

type Tab = "pulls" | "issues";
type PullFilter = "open" | "review" | "mine" | "fleet" | "closed";

const props = defineProps<{
  owner: string;
  repo: string;
}>();

const router = useRouter();
const smartLinks = useSmartLinksStore();
const { sessionsFor, openSession, startSession } = useGitHubSessions();
const { work, load: loadWork } = useGitHubWork();
onMounted(() => void loadWork());

const ownerRef = computed(() => props.owner);
const repoRef = computed(() => props.repo);
const repoFullName = computed(() => `${props.owner}/${props.repo}`);
const scope = computed(() => `repo:${repoFullName.value}`);

const activeTab = shallowRef<Tab>("pulls");
const counts = computed(() => work.value?.repos.find((repo) => repo.fullName.toLowerCase() === repoFullName.value.toLowerCase()) ?? null);

// ── Pull requests ──────────────────────────────────────────────────────────────

const PULL_FILTERS: { id: PullFilter; label: string; qualifier: string }[] = [
  { id: "open", label: "Open", qualifier: "is:open" },
  { id: "review", label: "Needs my review", qualifier: "is:open review-requested:@me" },
  { id: "mine", label: "Mine", qualifier: "is:open author:@me" },
  { id: "fleet", label: "In Fleet", qualifier: "" },
  { id: "closed", label: "Closed", qualifier: "is:closed" },
];
const pullFilter = shallowRef<PullFilter>("open");
const pullText = shallowRef("");

const pullQuery = computed(() => {
  if (activeTab.value !== "pulls" || pullFilter.value === "fleet") return null;
  const qualifier = PULL_FILTERS.find((f) => f.id === pullFilter.value)?.qualifier ?? "";
  return `${scope.value} is:pr ${qualifier} ${pullText.value.trim()} sort:updated-desc`.replace(/\s+/g, " ").trim();
});
const pulls = useGitHubSearch(pullQuery);

// "In Fleet": the pull requests sessions have links to, fetched by number.
const fleetNumbers = computed(() => {
  const numbers = new Set<number>();
  const prefix = `${repoFullName.value.toLowerCase()}#`;
  for (const links of Object.values(smartLinks.headerBySession)) {
    for (const link of links) {
      const id = link.resourceId.toLowerCase();
      if (isPullRequest(link) && id.startsWith(prefix)) numbers.add(Number(id.slice(prefix.length)));
    }
  }
  return [...numbers].filter((n) => n > 0).sort((a, b) => b - a);
});
const fleetPulls = shallowRef<GitHubItemSummary[]>([]);
const fleetLoading = shallowRef(false);
const fleetError = shallowRef<string | null>(null);
watch(
  () => (activeTab.value === "pulls" && pullFilter.value === "fleet" ? fleetNumbers.value.join(",") : null),
  async (key, _previous, onCleanup) => {
    if (key === null) return;
    const controller = new AbortController();
    onCleanup(() => controller.abort());
    fleetLoading.value = true;
    fleetError.value = null;
    try {
      fleetPulls.value = await fetchGitHubItemsByNumber(props.owner, props.repo, fleetNumbers.value, controller.signal);
    } catch (error) {
      if (!controller.signal.aborted) fleetError.value = error instanceof Error ? error.message : "GitHub didn't answer.";
    } finally {
      if (!controller.signal.aborted) fleetLoading.value = false;
    }
  },
  { immediate: true },
);

// ── Issues ─────────────────────────────────────────────────────────────────────

const issueFilter = shallowRef<IssueFilterState>({ ...DEFAULT_ISSUE_FILTER });
const issueQuery = computed(() => (activeTab.value === "issues" ? `${scope.value} is:issue ${issueFilterQuery(issueFilter.value)}` : null));
const issues = useGitHubSearch(issueQuery);

const { data: labels, isLoading: labelsLoading } = useGitHubLabels({ owner: ownerRef, repo: repoRef });
const { data: milestones, isLoading: milestonesLoading } = useGitHubMilestones({ owner: ownerRef, repo: repoRef });
const { data: assignees, isLoading: assigneesLoading } = useGitHubAssignees({ owner: ownerRef, repo: repoRef });

function toggleLabel(label: string): void {
  if (activeTab.value === "pulls") {
    const qualifier = /\s/.test(label) ? `label:"${label}"` : `label:${label}`;
    pullText.value = pullText.value.includes(qualifier) ? pullText.value.replace(qualifier, "").trim() : `${pullText.value} ${qualifier}`.trim();
    return;
  }
  const current = issueFilter.value;
  const next = current.labels.includes(label) ? current.labels.filter((l) => l !== label) : [...current.labels, label];
  issueFilter.value = { ...current, labels: next };
}

// ── The list on screen ─────────────────────────────────────────────────────────

const list = computed(() => {
  if (activeTab.value === "issues") {
    return { items: issues.items.value, loading: issues.isLoading.value, error: issues.error.value, hasMore: issues.hasMore.value, loadingMore: issues.isLoadingMore.value };
  }
  if (pullFilter.value === "fleet") {
    return { items: fleetPulls.value, loading: fleetLoading.value, error: fleetError.value, hasMore: false, loadingMore: false };
  }
  return { items: pulls.items.value, loading: pulls.isLoading.value, error: pulls.error.value, hasMore: pulls.hasMore.value, loadingMore: pulls.isLoadingMore.value };
});

const emptyWords = computed(() => {
  if (activeTab.value === "issues") return "No issues match.";
  switch (pullFilter.value) {
    case "review": return "Nobody is waiting on your review here.";
    case "mine": return "You have no open pull requests here.";
    case "fleet": return "No session is working on a pull request here.";
    case "closed": return "No closed pull requests match.";
    default: return "No open pull requests match.";
  }
});

function refresh(): void {
  if (activeTab.value === "issues") void issues.refresh();
  else if (pullFilter.value !== "fleet") void pulls.refresh();
}

function loadMore(): void {
  if (activeTab.value === "issues") void issues.loadMore();
  else void pulls.loadMore();
}

function sessions(item: GitHubItemSummary): SessionListItem[] {
  return sessionsFor(itemResourceId(item));
}

function open(item: GitHubItemSummary): void {
  void router.navigate({ to: itemRoute(item) as string });
}

function start(item: GitHubItemSummary): void {
  void startSession(itemSessionPreset(item));
}
</script>

<template>
  <div class="gh-repo">
    <header class="gh-repo__head">
      <h1 class="gh-repo__name">
        <span class="gh-repo__owner">{{ owner }} /</span> {{ repo }}
      </h1>
      <button
        type="button"
        class="gh-repo__btn gh-repo__btn--ghost"
        :disabled="list.loading"
        aria-label="Refresh"
        title="Refresh"
        @click="refresh"
      >
        <RefreshCw
          :size="14"
          :class="{ 'gh-repo__spin': list.loading }"
          aria-hidden="true"
        />
      </button>
      <a
        class="gh-repo__btn"
        :href="`https://github.com/${repoFullName}`"
        target="_blank"
        rel="noreferrer noopener"
      >
        <ExternalLink
          :size="13"
          aria-hidden="true"
        />
        GitHub
      </a>
    </header>

    <div
      class="gh-repo__tabs"
      role="tablist"
      aria-label="Pull requests and issues"
    >
      <button
        type="button"
        role="tab"
        class="gh-repo__tab"
        :aria-selected="activeTab === 'pulls'"
        @click="activeTab = 'pulls'"
      >
        <GitPullRequest
          :size="14"
          aria-hidden="true"
        />
        Pull requests
        <span
          v-if="counts"
          class="gh-repo__count"
        >{{ counts.openPullRequests }}</span>
      </button>
      <button
        type="button"
        role="tab"
        class="gh-repo__tab"
        :aria-selected="activeTab === 'issues'"
        @click="activeTab = 'issues'"
      >
        <CircleDot
          :size="14"
          aria-hidden="true"
        />
        Issues
        <span
          v-if="counts"
          class="gh-repo__count"
        >{{ counts.openIssues }}</span>
      </button>
    </div>

    <div
      v-if="activeTab === 'pulls'"
      class="gh-repo__filters"
    >
      <button
        v-for="filter in PULL_FILTERS"
        :key="filter.id"
        type="button"
        class="gh-repo__chip"
        :aria-pressed="pullFilter === filter.id"
        :data-testid="`github-pull-filter-${filter.id}`"
        @click="pullFilter = filter.id"
      >
        {{ filter.label }}
        <span
          v-if="filter.id === 'fleet' && fleetNumbers.length"
          class="gh-repo__chip-count"
        >{{ fleetNumbers.length }}</span>
      </button>
      <label class="gh-repo__search">
        <Search
          :size="13"
          aria-hidden="true"
        />
        <input
          v-model="pullText"
          type="search"
          placeholder="Filter, e.g. label:bug author:octocat"
          aria-label="Filter pull requests"
          :disabled="pullFilter === 'fleet'"
        >
      </label>
    </div>

    <IssueFilterBar
      v-else
      :filter="issueFilter"
      :is-searching="issues.isLoading.value"
      :labels="(labels as GitHubLabel[])"
      :labels-loading="labelsLoading"
      :milestones="(milestones as GitHubMilestone[])"
      :milestones-loading="milestonesLoading"
      :assignees="(assignees as GitHubAssignee[])"
      :assignees-loading="assigneesLoading"
      @change="issueFilter = $event"
    />

    <div class="gh-repo__list">
      <div
        v-if="list.loading && list.items.length === 0"
        class="gh-repo__state"
      >
        <LoaderCircle
          :size="16"
          class="gh-repo__spin"
          aria-hidden="true"
        />
      </div>
      <div
        v-else-if="list.error"
        class="gh-repo__state gh-repo__state--error"
        role="alert"
      >
        <TriangleAlert
          :size="14"
          aria-hidden="true"
        />
        {{ list.error }}
        <button
          type="button"
          class="gh-repo__btn"
          @click="refresh"
        >
          Try again
        </button>
      </div>
      <p
        v-else-if="list.items.length === 0"
        class="gh-repo__state"
      >
        {{ emptyWords }}
      </p>
      <template v-else>
        <GitHubItemRow
          v-for="item in list.items"
          :key="itemResourceId(item)"
          :item="item"
          :sessions="sessions(item)"
          @open="open"
          @start="start"
          @open-session="openSession"
          @label="toggleLabel"
        />
        <button
          v-if="list.hasMore"
          type="button"
          class="gh-repo__more"
          :disabled="list.loadingMore"
          @click="loadMore"
        >
          {{ list.loadingMore ? "Loading…" : "Load more" }}
        </button>
      </template>
    </div>
  </div>
</template>

<style scoped>
.gh-repo {
  display: flex;
  flex-direction: column;
  gap: 12px;
  max-width: 1080px;
  min-height: 100%;
  margin: 0 auto;
  color: var(--text);
}

.gh-repo__head {
  display: flex;
  align-items: center;
  gap: 6px;
}

.gh-repo__name {
  flex: 1;
  min-width: 0;
  margin: 0;
  overflow: hidden;
  font-size: 20px;
  font-weight: 600;
  letter-spacing: -0.01em;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.gh-repo__owner {
  color: var(--muted);
  font-weight: 400;
}

.gh-repo__btn {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  height: 30px;
  padding: 0 11px;
  border: 1px solid var(--border);
  border-radius: var(--radius-btn);
  background: var(--card-bg);
  color: var(--text);
  font-size: 12.5px;
  font-weight: 500;
  text-decoration: none;
  white-space: nowrap;
  cursor: pointer;
}

.gh-repo__btn--ghost {
  border-color: transparent;
  background: transparent;
  color: var(--muted);
}

.gh-repo__btn:focus-visible,
.gh-repo__tab:focus-visible,
.gh-repo__chip:focus-visible,
.gh-repo__more:focus-visible {
  outline: 2px solid var(--accent);
  outline-offset: 1px;
}

.gh-repo__tabs {
  display: flex;
  gap: 4px;
  border-bottom: 1px solid var(--border);
}

.gh-repo__tab {
  display: inline-flex;
  align-items: center;
  gap: 7px;
  margin-bottom: -1px;
  padding: 8px 12px;
  border: 0;
  border-bottom: 2px solid transparent;
  background: transparent;
  color: var(--muted);
  font-size: 13px;
  font-weight: 500;
  cursor: pointer;
}

.gh-repo__tab:hover {
  color: var(--text);
}

.gh-repo__tab[aria-selected="true"] {
  border-bottom-color: var(--accent);
  color: var(--text);
}

.gh-repo__count,
.gh-repo__chip-count {
  color: color-mix(in srgb, var(--muted) 80%, transparent);
  font-family: var(--font-mono-stack);
  font-size: 11px;
  font-weight: 400;
}

.gh-repo__filters {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 6px;
}

.gh-repo__chip {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  height: 28px;
  padding: 0 10px;
  border: 1px solid var(--border);
  border-radius: var(--radius-btn);
  background: transparent;
  color: var(--muted);
  font-size: 12.5px;
  cursor: pointer;
}

.gh-repo__chip:hover {
  color: var(--text);
}

.gh-repo__chip[aria-pressed="true"] {
  border-color: transparent;
  background: var(--accent-dim);
  color: var(--text);
}

.gh-repo__search {
  display: flex;
  flex: 1;
  align-items: center;
  gap: 8px;
  min-width: 220px;
  height: 30px;
  padding: 0 10px;
  border: 1px solid var(--border);
  border-radius: var(--radius-btn);
  background: color-mix(in srgb, var(--text) 3%, transparent);
  color: var(--muted);
}

.gh-repo__search input {
  flex: 1;
  min-width: 0;
  border: 0;
  background: transparent;
  color: var(--text);
  font-family: var(--font-mono-stack);
  font-size: 12px;
  outline: none;
}

.gh-repo__list {
  display: grid;
  gap: 1px;
}

.gh-repo__state {
  display: flex;
  justify-content: center;
  align-items: center;
  gap: 8px;
  margin: 0;
  padding: 40px 16px;
  color: var(--muted);
  font-size: 13px;
}

.gh-repo__state--error {
  color: var(--text);
}

.gh-repo__state--error > svg {
  color: var(--error);
}

.gh-repo__more {
  justify-self: center;
  margin-top: 8px;
  padding: 6px 12px;
  border: 0;
  border-radius: var(--radius-btn);
  background: transparent;
  color: var(--muted);
  font-size: 12.5px;
  cursor: pointer;
}

.gh-repo__more:hover {
  background: color-mix(in srgb, var(--text) 5%, transparent);
  color: var(--text);
}

.gh-repo__spin {
  animation: gh-repo-spin 1s linear infinite;
}

@keyframes gh-repo-spin {
  to { transform: rotate(360deg); }
}

@media (prefers-reduced-motion: reduce) {
  .gh-repo__spin { animation: none; }
}
</style>
