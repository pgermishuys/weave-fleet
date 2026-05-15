<script setup lang="ts">
import { computed, shallowRef } from "vue";
import {
  CircleDot,
  GitPullRequest,
  RefreshCw,
  Loader2,
  ArrowLeft,
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
      <button class="back-btn" @click="goBack">
        <ArrowLeft :size="14" />
      </button>
      <span class="repo-name">{{ repoFullName }}</span>
    </div>

    <!-- Tabs -->
    <div class="tab-bar">
      <button
        :class="['tab-btn', activeTab === 'issues' && 'tab-btn--active']"
        @click="activeTab = 'issues'"
      >
        <CircleDot :size="13" />
        Issues
        <span v-if="issues.length > 0" class="tab-count">{{ issues.length }}</span>
      </button>
      <button
        :class="['tab-btn', activeTab === 'pulls' && 'tab-btn--active']"
        @click="activeTab = 'pulls'"
      >
        <GitPullRequest :size="13" />
        Pull Requests
        <span v-if="pulls.length > 0" class="tab-count">{{ pulls.length }}</span>
      </button>
    </div>

    <!-- Issues Tab -->
    <div v-if="activeTab === 'issues'" class="tab-content">
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

      <div v-if="issuesLoading && issues.length === 0" class="list-loading">
        <Loader2 :size="16" class="animate-spin" />
      </div>

      <div v-else-if="issuesError" class="list-error">
        <p>{{ issuesError }}</p>
        <button class="retry-btn" @click="refetchIssues">Retry</button>
      </div>

      <div v-else-if="!issuesLoading && issues.length === 0" class="list-empty">
        No issues found.
      </div>

      <template v-else>
        <IssueItem
          v-for="item in issueItems"
          :key="item.id"
          :item="item"
          @label-click="handleLabelClick"
        />

        <div v-if="issuesLoading" class="list-loading-more">
          <Loader2 :size="14" class="animate-spin" />
        </div>
        <button
          v-else-if="issuesHasMore"
          class="load-more-btn"
          @click="loadMoreIssues"
        >
          Load more
        </button>
      </template>
    </div>

    <!-- PRs Tab -->
    <div v-if="activeTab === 'pulls'" class="tab-content">
      <div class="pr-filter-bar">
        <button
          :class="['state-btn', pullsStateFilter === 'open' && 'state-btn--active']"
          @click="pullsStateFilter = 'open'"
        >
          <GitPullRequest :size="12" />
          Open
        </button>
        <button
          :class="['state-btn', pullsStateFilter === 'closed' && 'state-btn--active']"
          @click="pullsStateFilter = 'closed'"
        >
          Closed
        </button>
        <button class="refresh-btn" title="Refresh" @click="refetchPulls">
          <RefreshCw :size="12" />
        </button>
      </div>

      <div v-if="pullsLoading && pulls.length === 0" class="list-loading">
        <Loader2 :size="16" class="animate-spin" />
      </div>

      <div v-else-if="pullsError" class="list-error">
        <p>{{ pullsError }}</p>
        <button class="retry-btn" @click="refetchPulls">Retry</button>
      </div>

      <div v-else-if="!pullsLoading && pulls.length === 0" class="list-empty">
        No pull requests found.
      </div>

      <template v-else>
        <PullRequestItem
          v-for="item in pullItems"
          :key="item.id"
          :item="item"
        />

        <div v-if="pullsLoading" class="list-loading-more">
          <Loader2 :size="14" class="animate-spin" />
        </div>
        <button
          v-else-if="pullsHasMore"
          class="load-more-btn"
          @click="loadMorePulls"
        >
          Load more
        </button>
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
  padding: 12px 16px 8px;
  border-bottom: 1px solid var(--border);
  flex-shrink: 0;
}

.back-btn {
  display: inline-flex;
  align-items: center;
  padding: 4px;
  border: none;
  background: transparent;
  color: var(--muted);
  cursor: pointer;
  border-radius: 4px;
}

.back-btn:hover {
  color: var(--text);
  background: var(--sidebar-item-hover);
}

.repo-name {
  font-size: 14px;
  font-weight: 600;
  color: var(--text);
}

/* ─── Tabs ────────────────────────────────────────────────────────────────── */
.tab-bar {
  display: flex;
  gap: 0;
  border-bottom: 1px solid var(--border);
  padding: 0 12px;
  flex-shrink: 0;
}

.tab-btn {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  padding: 8px 10px 7px;
  border: none;
  background: transparent;
  color: var(--muted);
  font-size: 12px;
  font-weight: 500;
  cursor: pointer;
  border-bottom: 2px solid transparent;
  margin-bottom: -1px;
}

.tab-btn:hover {
  color: var(--text);
}

.tab-btn--active {
  color: var(--text);
  border-bottom-color: var(--accent);
}

.tab-count {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  min-width: 18px;
  height: 16px;
  padding: 0 5px;
  border-radius: 999px;
  background: var(--sidebar-item-hover);
  font-size: 10px;
  font-weight: 600;
  color: var(--muted);
}

/* ─── Tab content ─────────────────────────────────────────────────────────── */
.tab-content {
  flex: 1;
  overflow-y: auto;
  display: flex;
  flex-direction: column;
}

/* ─── PR filter bar ───────────────────────────────────────────────────────── */
.pr-filter-bar {
  display: flex;
  align-items: center;
  gap: 2px;
  padding: 6px 12px;
  border-bottom: 1px solid var(--border);
}

.state-btn {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  padding: 2px 8px;
  height: 28px;
  border-radius: 6px;
  border: none;
  background: transparent;
  color: var(--muted);
  font-size: 11px;
  cursor: pointer;
}

.state-btn:hover {
  background: var(--sidebar-item-hover);
  color: var(--text);
}

.state-btn--active {
  background: var(--accent-muted, rgba(99, 102, 241, 0.15));
  color: var(--accent);
}

.refresh-btn {
  display: inline-flex;
  align-items: center;
  padding: 4px 6px;
  border: none;
  background: transparent;
  color: var(--muted);
  cursor: pointer;
  border-radius: 4px;
  margin-left: auto;
}

.refresh-btn:hover {
  color: var(--text);
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
  font-size: 12px;
  color: var(--muted);
}

.retry-btn {
  padding: 4px 12px;
  border-radius: 6px;
  border: 1px solid var(--border);
  background: transparent;
  color: var(--text);
  font-size: 11px;
  cursor: pointer;
}

.retry-btn:hover {
  background: var(--sidebar-item-hover);
}

.load-more-btn {
  width: 100%;
  padding: 10px;
  border: none;
  background: transparent;
  color: var(--accent);
  font-size: 12px;
  cursor: pointer;
  border-top: 1px solid var(--border);
}

.load-more-btn:hover {
  background: var(--sidebar-item-hover);
}
</style>
