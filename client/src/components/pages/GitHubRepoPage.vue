<script setup lang="ts">
import { computed, shallowRef } from "vue";
import {
  CircleDot,
  GitPullRequest,
  RefreshCw,
  Loader2,
  ArrowLeft,
  ExternalLink,
} from "lucide-vue-next";
import { useRouter } from "@tanstack/vue-router";
import { useGitHubIssues } from "@/plugins/builtin/github/composables/use-github-issues";
import { useGitHubPulls } from "@/plugins/builtin/github/composables/use-github-pulls";
import {
  useGitHubLabels,
  useGitHubMilestones,
  useGitHubAssignees,
} from "@/plugins/builtin/github/composables/use-github-metadata";
import { DEFAULT_ISSUE_FILTER, type IssueFilterState, type GitHubLabel, type GitHubMilestone, type GitHubAssignee } from "@/plugins/builtin/github/composables/github-types";
import IssueItem from "@/plugins/builtin/github/IssueItem.vue";
import PullRequestItem from "@/plugins/builtin/github/PullRequestItem.vue";
import IssueFilterBar from "@/plugins/builtin/github/components/IssueFilterBar.vue";
import { Button } from "@/components/ui/button";

type Tab = "issues" | "pulls";

const props = defineProps<{
  owner: string;
  repo: string;
}>();

const router = useRouter();

const ownerRef = computed(() => props.owner);
const repoRef = computed(() => props.repo);
const repoFullName = computed(() => `${props.owner}/${props.repo}`);

// ─── Tabs ─────────────────────────────────────────────────────────────────────
const activeTab = shallowRef<Tab>("issues");

// ─── Issue filter ─────────────────────────────────────────────────────────────
const issueFilter = shallowRef<IssueFilterState>({ ...DEFAULT_ISSUE_FILTER });

function handleFilterChange(f: IssueFilterState) {
  issueFilter.value = f;
}

function handleLabelClick(label: string) {
  const current = issueFilter.value;
  const labels = current.labels.includes(label)
    ? current.labels.filter((l) => l !== label)
    : [...current.labels, label];
  issueFilter.value = { ...current, labels };
}

// ─── Issues ───────────────────────────────────────────────────────────────────
const { data: labels, isLoading: labelsLoading } = useGitHubLabels({ owner: ownerRef, repo: repoRef });
const { data: milestones, isLoading: milestonesLoading } = useGitHubMilestones({ owner: ownerRef, repo: repoRef });
const { data: assignees, isLoading: assigneesLoading } = useGitHubAssignees({ owner: ownerRef, repo: repoRef });

const {
  issues,
  isLoading: issuesLoading,
  isSearching,
  error: issuesError,
  hasMore: issuesHasMore,
  loadMore: loadMoreIssues,
  refetch: refetchIssues,
} = useGitHubIssues({
  owner: ownerRef,
  repo: repoRef,
  filter: issueFilter,
  milestones,
});

// ─── Pull Requests ─────────────────────────────────────────────────────────────
const pullsStateFilter = shallowRef<"open" | "closed">("open");

const {
  pulls,
  isLoading: pullsLoading,
  error: pullsError,
  hasMore: pullsHasMore,
  loadMore: loadMorePulls,
  refetch: refetchPulls,
} = useGitHubPulls({
  owner: ownerRef,
  repo: repoRef,
  filter: computed(() => ({ state: pullsStateFilter.value })),
});

// ─── Map API objects to item shapes ──────────────────────────────────────────
const issueItems = computed(() =>
  issues.value.map((issue) => ({
    id: issue.id,
    number: issue.number,
    title: issue.title,
    state: issue.state,
    repoFullName: repoFullName.value,
    labels: issue.labels,
    user: { login: issue.user.login, avatarUrl: issue.user.avatar_url },
    comments: issue.comments,
    updatedAt: issue.updated_at,
    htmlUrl: issue.html_url,
  })),
);

const pullItems = computed(() =>
  pulls.value.map((pr) => ({
    id: pr.id,
    number: pr.number,
    title: pr.title,
    state: pr.merged_at ? "merged" as const : pr.state,
    draft: pr.draft,
    repoFullName: repoFullName.value,
    labels: pr.labels,
    user: { login: pr.user.login, avatarUrl: pr.user.avatar_url },
    comments: pr.comments,
    updatedAt: pr.updated_at,
    htmlUrl: pr.html_url,
    headBranch: pr.head.ref,
  })),
);

function goBack() {
  void router.navigate({ to: "/github" });
}
</script>

<template>
  <div class="github-repo-page">
    <!-- Header -->
    <div class="repo-header">
      <Button
        variant="toolbar-icon"
        size="toolbar"
        aria-label="Back to repositories"
        title="Back to repositories"
        @click="goBack"
      >
        <ArrowLeft :size="15" />
      </Button>
      <h1 class="repo-name">
        <span class="repo-name__owner">{{ owner }} /</span>
        {{ repo }}
      </h1>
      <a
        class="repo-link"
        :href="`https://github.com/${repoFullName}`"
        target="_blank"
        rel="noreferrer noopener"
      >
        Open on GitHub
        <ExternalLink
          :size="12"
          aria-hidden="true"
        />
      </a>
    </div>

    <!-- Tabs -->
    <div
      class="tab-bar"
      role="tablist"
      aria-label="Issues and pull requests"
    >
      <button
        :class="['tab-btn', activeTab === 'issues' && 'tab-btn--active']"
        role="tab"
        :aria-selected="activeTab === 'issues'"
        @click="activeTab = 'issues'"
      >
        <CircleDot :size="13" />
        Issues
        <span
          v-if="issues.length > 0"
          class="tab-count"
        >{{ issues.length }}</span>
      </button>
      <button
        :class="['tab-btn', activeTab === 'pulls' && 'tab-btn--active']"
        role="tab"
        :aria-selected="activeTab === 'pulls'"
        @click="activeTab = 'pulls'"
      >
        <GitPullRequest :size="13" />
        Pull requests
        <span
          v-if="pulls.length > 0"
          class="tab-count"
        >{{ pulls.length }}</span>
      </button>
    </div>

    <!-- Issues Tab -->
    <div
      v-if="activeTab === 'issues'"
      class="tab-content"
    >
      <IssueFilterBar
        :filter="issueFilter"
        :is-searching="isSearching"
        :labels="(labels as GitHubLabel[])"
        :labels-loading="labelsLoading"
        :milestones="(milestones as GitHubMilestone[])"
        :milestones-loading="milestonesLoading"
        :assignees="(assignees as GitHubAssignee[])"
        :assignees-loading="assigneesLoading"
        @change="handleFilterChange"
      />

      <div
        v-if="issuesLoading && issues.length === 0"
        class="list-loading"
      >
        <Loader2
          :size="16"
          class="animate-spin"
        />
      </div>

      <div
        v-else-if="issuesError"
        class="list-error"
      >
        <p>{{ issuesError }}</p>
        <Button
          variant="outline"
          size="sm"
          @click="refetchIssues"
        >
          Retry
        </Button>
      </div>

      <div
        v-else-if="!issuesLoading && issues.length === 0"
        class="list-empty"
      >
        No issues found.
      </div>

      <template v-else>
        <IssueItem
          v-for="item in issueItems"
          :key="item.id"
          :item="item"
          @label-click="handleLabelClick"
        />

        <div
          v-if="issuesLoading"
          class="list-loading-more"
        >
          <Loader2
            :size="14"
            class="animate-spin"
          />
        </div>
        <Button
          v-else-if="issuesHasMore"
          variant="ghost"
          size="sm"
          class="load-more-btn"
          @click="loadMoreIssues"
        >
          Load more
        </Button>
      </template>
    </div>

    <!-- PRs Tab -->
    <div
      v-if="activeTab === 'pulls'"
      class="tab-content"
    >
      <div class="pr-filter-bar">
        <Button
          variant="filter"
          size="sm"
          :data-active="pullsStateFilter === 'open'"
          @click="pullsStateFilter = 'open'"
        >
          <GitPullRequest :size="12" />
          Open
        </Button>
        <Button
          variant="filter"
          size="sm"
          :data-active="pullsStateFilter === 'closed'"
          @click="pullsStateFilter = 'closed'"
        >
          Closed
        </Button>
        <Button
          variant="toolbar-icon"
          size="toolbar"
          title="Refresh"
          @click="refetchPulls"
        >
          <RefreshCw :size="12" />
        </Button>
      </div>

      <div
        v-if="pullsLoading && pulls.length === 0"
        class="list-loading"
      >
        <Loader2
          :size="16"
          class="animate-spin"
        />
      </div>

      <div
        v-else-if="pullsError"
        class="list-error"
      >
        <p>{{ pullsError }}</p>
        <Button
          variant="outline"
          size="sm"
          @click="refetchPulls"
        >
          Retry
        </Button>
      </div>

      <div
        v-else-if="!pullsLoading && pulls.length === 0"
        class="list-empty"
      >
        No pull requests found.
      </div>

      <template v-else>
        <PullRequestItem
          v-for="item in pullItems"
          :key="item.id"
          :item="item"
        />

        <div
          v-if="pullsLoading"
          class="list-loading-more"
        >
          <Loader2
            :size="14"
            class="animate-spin"
          />
        </div>
        <Button
          v-else-if="pullsHasMore"
          variant="ghost"
          size="sm"
          class="load-more-btn"
          @click="loadMorePulls"
        >
          Load more
        </Button>
      </template>
    </div>
  </div>
</template>

<style scoped>
.github-repo-page {
  display: flex;
  flex-direction: column;
  height: 100%;
  overflow: visible;
}

/* ─── Header ──────────────────────────────────────────────────────────────── */
.repo-header {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 0 0 12px;
  flex-shrink: 0;
}

.repo-name {
  flex: 1;
  min-width: 0;
  margin: 0;
  overflow: hidden;
  color: var(--text);
  font-size: 18px;
  font-weight: 600;
  letter-spacing: -0.01em;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.repo-name__owner {
  color: var(--muted);
  font-weight: 500;
}

.repo-link {
  display: inline-flex;
  flex-shrink: 0;
  align-items: center;
  gap: 5px;
  height: 28px;
  padding: 0 8px;
  border-radius: var(--radius-btn);
  color: var(--muted);
  font-size: 12.5px;
  text-decoration: none;
  transition: background-color var(--transition), color var(--transition);
}

.repo-link:hover {
  background: color-mix(in srgb, var(--text) 6%, transparent);
  color: var(--text);
}

/* ─── Tabs: pills, like the canvas tabs ───────────────────────────────────── */
.tab-bar {
  display: flex;
  gap: 2px;
  padding-bottom: 10px;
  border-bottom: 1px solid var(--border);
  flex-shrink: 0;
}

.tab-btn {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  height: 28px;
  padding: 0 10px;
  border: none;
  border-radius: var(--radius-btn);
  background: transparent;
  color: var(--muted);
  font-size: 13px;
  font-weight: 500;
  cursor: pointer;
  transition: background-color var(--transition), color var(--transition);
}

.tab-btn:hover {
  background: color-mix(in srgb, var(--text) 5%, transparent);
  color: var(--text);
}

.tab-btn--active {
  background: color-mix(in srgb, var(--text) 9%, transparent);
  color: var(--text);
}

.tab-count {
  color: var(--muted);
  font-size: 12px;
  font-weight: 500;
  font-variant-numeric: tabular-nums;
}

/* ─── Tab content ─────────────────────────────────────────────────────────── */
.tab-content {
  flex: 1;
  display: flex;
  flex-direction: column;
  gap: 1px;
  padding-top: 6px;
}

/* ─── PR filter bar ───────────────────────────────────────────────────────── */
.pr-filter-bar {
  display: flex;
  align-items: center;
  gap: 2px;
  padding: 0 4px 6px;
}

/* ─── List states ─────────────────────────────────────────────────────────── */
.list-loading {
  display: flex;
  justify-content: center;
  align-items: center;
  padding: 40px;
  color: var(--muted);
}

.list-loading-more {
  display: flex;
  justify-content: center;
  padding: 12px;
  color: var(--muted);
}

.list-error {
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: 8px;
  padding: 24px 16px;
  color: var(--muted);
  font-size: 12px;
  text-align: center;
}

.list-empty {
  padding: 40px 16px;
  text-align: center;
  font-size: 13px;
  color: var(--muted);
}

.load-more-btn {
  align-self: center;
  margin-top: 6px;
  color: var(--muted);
}
</style>
