<script setup lang="ts">
import { computed, shallowRef, useTemplateRef, watch } from "vue";
import {
  CircleDot,
  GitPullRequest,
  RefreshCw,
  Search,
  ChevronDown,
  Loader2,
  Bookmark,
  BookmarkX,
} from "lucide-vue-next";
import { useGitHubAuth } from "@/plugins/builtin/github/composables/use-github-auth";
import { useGitHubRepos } from "@/plugins/builtin/github/composables/use-github-repos";
import { useGitHubBookmarks } from "@/plugins/builtin/github/composables/use-github-bookmarks";
import { useGitHubIssues } from "@/plugins/builtin/github/composables/use-github-issues";
import { useGitHubPulls } from "@/plugins/builtin/github/composables/use-github-pulls";
import {
  useGitHubLabels,
  useGitHubMilestones,
  useGitHubAssignees,
} from "@/plugins/builtin/github/composables/use-github-metadata";
import { DEFAULT_ISSUE_FILTER, type IssueFilterState, type CachedGitHubRepo, type GitHubLabel, type GitHubMilestone, type GitHubAssignee } from "@/plugins/builtin/github/composables/github-types";
import IssueItem from "@/plugins/builtin/github/IssueItem.vue";
import PullRequestItem from "@/plugins/builtin/github/PullRequestItem.vue";
import IssueFilterBar from "@/plugins/builtin/github/components/IssueFilterBar.vue";

type Tab = "issues" | "pulls";

// ─── Auth ─────────────────────────────────────────────────────────────────────
const { isConnected, isLoadingStatus } = useGitHubAuth();

// ─── Repos & Bookmarks ────────────────────────────────────────────────────────
const { repos, isLoading: isLoadingRepos, refresh: refreshRepos } = useGitHubRepos({ autoLoad: false });
const { bookmarks, addBookmark, removeBookmark, hasBookmark } = useGitHubBookmarks();

// ─── Repo Selector State ──────────────────────────────────────────────────────
const selectedRepoFullName = shallowRef<string | null>(null);
const repoFilterQuery = shallowRef("");
const isRepoDropdownOpen = shallowRef(false);
const repoSelectorRef = useTemplateRef<HTMLDivElement>("repoSelector");

const selectedRepo = computed<CachedGitHubRepo | null>(() => {
  if (!selectedRepoFullName.value) return null;
  return repos.value.find((r) => r.full_name === selectedRepoFullName.value)
    ?? (bookmarks.value.find((b) => b.fullName === selectedRepoFullName.value)
      ? { full_name: selectedRepoFullName.value, name: selectedRepoFullName.value.split("/")[1] ?? "", owner_login: selectedRepoFullName.value.split("/")[0] ?? "", id: 0, private: false, language: null, stargazers_count: 0 }
      : null);
});

const selectedOwner = computed(() => selectedRepo.value?.owner_login ?? selectedRepoFullName.value?.split("/")[0] ?? null);
const selectedRepoName = computed(() => selectedRepo.value?.name ?? selectedRepoFullName.value?.split("/")[1] ?? null);

const filteredRepos = computed(() => {
  const q = repoFilterQuery.value.trim().toLowerCase();
  const all = repos.value;
  if (!q) return all;
  return all.filter((r) => r.full_name.toLowerCase().includes(q));
});

function selectRepo(fullName: string) {
  selectedRepoFullName.value = fullName;
  isRepoDropdownOpen.value = false;
  repoFilterQuery.value = "";
}

function openRepoDropdown() {
  if (!isLoadingRepos.value && repos.value.length === 0) {
    void refreshRepos();
  }
  isRepoDropdownOpen.value = !isRepoDropdownOpen.value;
}

function handleRepoDropdownClickOutside(e: MouseEvent) {
  if (repoSelectorRef.value && !repoSelectorRef.value.contains(e.target as Node)) {
    isRepoDropdownOpen.value = false;
  }
}

watch(isRepoDropdownOpen, (open) => {
  if (open) {
    document.addEventListener("mousedown", handleRepoDropdownClickOutside);
  } else {
    document.removeEventListener("mousedown", handleRepoDropdownClickOutside);
  }
});

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
const { data: labels, isLoading: labelsLoading } = useGitHubLabels({ owner: selectedOwner, repo: selectedRepoName });
const { data: milestones, isLoading: milestonesLoading } = useGitHubMilestones({ owner: selectedOwner, repo: selectedRepoName });
const { data: assignees, isLoading: assigneesLoading } = useGitHubAssignees({ owner: selectedOwner, repo: selectedRepoName });

const {
  issues,
  isLoading: issuesLoading,
  isSearching,
  error: issuesError,
  hasMore: issuesHasMore,
  loadMore: loadMoreIssues,
  refetch: refetchIssues,
} = useGitHubIssues({
  owner: selectedOwner,
  repo: selectedRepoName,
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
  owner: selectedOwner,
  repo: selectedRepoName,
  filter: computed(() => ({ state: pullsStateFilter.value })),
});

// ─── Map API objects to item shapes ──────────────────────────────────────────
const issueItems = computed(() =>
  issues.value.map((issue) => ({
    id: issue.id,
    number: issue.number,
    title: issue.title,
    state: issue.state,
    repoFullName: selectedRepoFullName.value ?? "",
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
    repoFullName: selectedRepoFullName.value ?? "",
    labels: pr.labels,
    user: { login: pr.user.login, avatarUrl: pr.user.avatar_url },
    comments: pr.comments,
    updatedAt: pr.updated_at,
    htmlUrl: pr.html_url,
  })),
);

// ─── Bookmark toggle ──────────────────────────────────────────────────────────
async function toggleBookmark() {
  if (!selectedRepoFullName.value) return;
  const fullName = selectedRepoFullName.value;
  if (hasBookmark(fullName)) {
    await removeBookmark(fullName);
  } else {
    const repo = selectedRepo.value;
    await addBookmark({
      fullName,
      owner: fullName.split("/")[0] ?? "",
      name: repo?.name ?? fullName.split("/")[1] ?? "",
    });
  }
}
</script>

<template>
  <div class="github-browser">
    <!-- Header -->
    <div class="browser-header">
      <span class="browser-title">GitHub</span>
      <span v-if="isLoadingStatus" class="status-pill status-pill--loading">Checking…</span>
      <span v-else-if="isConnected" class="status-pill status-pill--connected">Connected</span>
      <span v-else class="status-pill status-pill--disconnected">Disconnected</span>
    </div>

    <!-- Not connected -->
    <div v-if="!isLoadingStatus && !isConnected" class="browser-empty">
      <p class="empty-title">GitHub is not connected.</p>
      <p class="empty-subtitle">Connect GitHub in Settings to browse repositories.</p>
    </div>

    <template v-else-if="isConnected">
      <!-- Repo Selector -->
      <div ref="repoSelector" class="repo-selector-wrap">
        <button class="repo-selector-btn" @click="openRepoDropdown">
          <span class="repo-selector-label">
            {{ selectedRepoFullName ?? "Select a repository…" }}
          </span>
          <div class="repo-selector-actions">
            <button
              v-if="selectedRepoFullName"
              class="bookmark-btn"
              :title="hasBookmark(selectedRepoFullName) ? 'Remove bookmark' : 'Bookmark repo'"
              @click.stop="toggleBookmark"
            >
              <BookmarkX v-if="hasBookmark(selectedRepoFullName)" :size="13" />
              <Bookmark v-else :size="13" />
            </button>
            <ChevronDown :size="13" class="repo-selector-chevron" />
          </div>
        </button>

        <div v-if="isRepoDropdownOpen" class="repo-dropdown">
          <div class="repo-search-wrap">
            <Search :size="12" class="repo-search-icon" />
            <input
              v-model="repoFilterQuery"
              class="repo-search-input"
              placeholder="Filter repositories…"
              autofocus
            />
          </div>

          <div class="repo-list">
            <!-- Bookmarks section -->
            <template v-if="bookmarks.length > 0 && !repoFilterQuery">
              <div class="repo-group-label">Bookmarked</div>
              <button
                v-for="bookmark in bookmarks"
                :key="bookmark.fullName"
                :class="['repo-option', selectedRepoFullName === bookmark.fullName && 'repo-option--selected']"
                @click="selectRepo(bookmark.fullName)"
              >
                {{ bookmark.fullName }}
              </button>
              <div v-if="filteredRepos.length > 0" class="repo-group-divider" />
            </template>

            <!-- All repos -->
            <template v-if="isLoadingRepos && filteredRepos.length === 0">
              <div class="repo-list-loading">
                <Loader2 :size="14" class="animate-spin" />
              </div>
            </template>
            <template v-else>
              <div class="repo-group-label">{{ repoFilterQuery ? 'Results' : 'All Repositories' }}</div>
              <button
                v-for="repo in filteredRepos"
                :key="repo.full_name"
                :class="['repo-option', selectedRepoFullName === repo.full_name && 'repo-option--selected']"
                @click="selectRepo(repo.full_name)"
              >
                {{ repo.full_name }}
              </button>
              <div v-if="filteredRepos.length === 0 && !isLoadingRepos" class="repo-list-empty">
                No repositories found.
              </div>
            </template>
          </div>
        </div>
      </div>

      <!-- No repo selected -->
      <div v-if="!selectedRepoFullName" class="browser-empty">
        <p class="empty-title">Select a repository to browse issues and pull requests.</p>
      </div>

      <!-- Tabs -->
      <template v-else>
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

          <!-- Loading state (first page) -->
          <div v-if="issuesLoading && issues.length === 0" class="list-loading">
            <Loader2 :size="16" class="animate-spin" />
          </div>

          <!-- Error state -->
          <div v-else-if="issuesError" class="list-error">
            <p>{{ issuesError }}</p>
            <button class="retry-btn" @click="refetchIssues">Retry</button>
          </div>

          <!-- Empty state -->
          <div v-else-if="!issuesLoading && issues.length === 0" class="list-empty">
            No issues found.
          </div>

          <!-- Items -->
          <template v-else>
            <IssueItem
              v-for="item in issueItems"
              :key="item.id"
              :item="item"
              @label-click="handleLabelClick"
            />

            <!-- Load more / loading more -->
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
          <!-- PR state toggle -->
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
      </template>
    </template>
  </div>
</template>

<style scoped>
.github-browser {
  display: flex;
  flex-direction: column;
  height: 100%;
  overflow: hidden;
}

/* ─── Header ──────────────────────────────────────────────────────────────── */
.browser-header {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 12px 16px 8px;
  border-bottom: 1px solid var(--border);
  flex-shrink: 0;
}

.browser-title {
  font-size: 14px;
  font-weight: 600;
  color: var(--text);
  flex: 1;
}

.status-pill {
  font-size: 10px;
  padding: 2px 8px;
  border-radius: 999px;
}

.status-pill--connected {
  background: rgba(34, 197, 94, 0.15);
  color: #22c55e;
}

.status-pill--disconnected {
  background: rgba(239, 68, 68, 0.15);
  color: #ef4444;
}

.status-pill--loading {
  background: var(--sidebar-item-hover);
  color: var(--muted);
}

/* ─── Repo selector ───────────────────────────────────────────────────────── */
.repo-selector-wrap {
  position: relative;
  flex-shrink: 0;
  padding: 8px 12px;
  border-bottom: 1px solid var(--border);
}

.repo-selector-btn {
  display: flex;
  align-items: center;
  justify-content: space-between;
  width: 100%;
  padding: 6px 10px;
  border: 1px solid var(--border);
  border-radius: 6px;
  background: transparent;
  color: var(--text);
  font-size: 12px;
  cursor: pointer;
  text-align: left;
}

.repo-selector-btn:hover {
  background: var(--sidebar-item-hover);
}

.repo-selector-label {
  flex: 1;
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.repo-selector-actions {
  display: flex;
  align-items: center;
  gap: 4px;
  flex-shrink: 0;
  margin-left: 4px;
}

.bookmark-btn {
  display: flex;
  align-items: center;
  padding: 2px;
  background: transparent;
  border: none;
  color: var(--muted);
  cursor: pointer;
  border-radius: 3px;
}

.bookmark-btn:hover {
  color: var(--text);
}

.repo-selector-chevron {
  color: var(--muted);
}

.repo-dropdown {
  position: absolute;
  top: calc(100% - 4px);
  left: 12px;
  right: 12px;
  z-index: 100;
  background: var(--sidebar);
  border: 1px solid var(--border);
  border-radius: 8px;
  box-shadow: 0 4px 20px rgba(0, 0, 0, 0.3);
  overflow: hidden;
  max-height: 320px;
  display: flex;
  flex-direction: column;
}

.repo-search-wrap {
  display: flex;
  align-items: center;
  gap: 6px;
  padding: 8px 10px;
  border-bottom: 1px solid var(--border);
  flex-shrink: 0;
}

.repo-search-icon {
  color: var(--muted);
  flex-shrink: 0;
}

.repo-search-input {
  flex: 1;
  background: transparent;
  border: none;
  outline: none;
  font-size: 12px;
  color: var(--text);
}

.repo-search-input::placeholder {
  color: var(--muted);
}

.repo-list {
  overflow-y: auto;
  flex: 1;
  padding: 4px 0;
}

.repo-group-label {
  padding: 4px 10px;
  font-size: 10px;
  font-weight: 600;
  color: var(--muted);
  text-transform: uppercase;
  letter-spacing: 0.05em;
}

.repo-group-divider {
  height: 1px;
  background: var(--border);
  margin: 4px 0;
}

.repo-option {
  display: block;
  width: 100%;
  padding: 6px 10px;
  text-align: left;
  font-size: 12px;
  background: transparent;
  border: none;
  color: var(--text);
  cursor: pointer;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.repo-option:hover {
  background: var(--sidebar-item-hover);
}

.repo-option--selected {
  color: var(--accent);
}

.repo-list-loading {
  display: flex;
  justify-content: center;
  padding: 12px;
  color: var(--muted);
}

.repo-list-empty {
  padding: 12px 10px;
  font-size: 12px;
  color: var(--muted);
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

/* ─── Empty/disconnected ──────────────────────────────────────────────────── */
.browser-empty {
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  flex: 1;
  padding: 32px 16px;
  text-align: center;
  gap: 6px;
}

.empty-title {
  font-size: 13px;
  color: var(--text);
}

.empty-subtitle {
  font-size: 11px;
  color: var(--muted);
}
</style>
