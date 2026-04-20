<script setup lang="ts">
import { computed, shallowRef, useTemplateRef, watch } from "vue";
import { useRouter } from "@tanstack/vue-router";
import { Github, RefreshCw, Search, Settings } from "lucide-vue-next";
import { useSidebarStore } from "@/stores/sidebar";
import type { CachedGitHubRepo, GitHubIssue, GitHubPullRequest } from "./composables/github-types";
import { useGitHubAuth } from "./composables/use-github-auth";
import { useGitHubBookmarks } from "./composables/use-github-bookmarks";
import { useGitHubIssues } from "./composables/use-github-issues";
import { useGitHubPulls } from "./composables/use-github-pulls";
import { useGitHubRepos } from "./composables/use-github-repos";
import IssueItem from "./IssueItem.vue";
import PullRequestItem from "./PullRequestItem.vue";

type GitHubTab = "issues" | "pull-requests";

interface GitHubLabel {
  name: string;
  color: string;
}

interface GitHubUser {
  login: string;
  avatarUrl: string;
}

interface GitHubIssuePanelItem {
  id: number;
  number: number;
  title: string;
  state: "open" | "closed";
  repoFullName: string;
  labels: readonly GitHubLabel[];
  user: GitHubUser;
  updatedAt: string;
  htmlUrl: string;
}

interface GitHubPullRequestPanelItem {
  id: number;
  number: number;
  title: string;
  state: "open" | "closed" | "merged";
  draft: boolean;
  repoFullName: string;
  labels: readonly GitHubLabel[];
  user: GitHubUser;
  updatedAt: string;
  htmlUrl: string;
}

interface RepoSelectorGroup {
  label: string;
  repos: readonly CachedGitHubRepo[];
}

const panelTabs = [
  { id: "issues", label: "Issues" },
  { id: "pull-requests", label: "Pull Requests" },
] as const satisfies readonly { id: GitHubTab; label: string }[];

const sidebarStore = useSidebarStore();
const router = useRouter();

const activeTab = shallowRef<GitHubTab>("issues");
const repoFilterQuery = shallowRef("");
const searchQuery = shallowRef("");
const selectedRepoFullName = shallowRef("");
const isRepoComboboxOpen = shallowRef(false);
const repoComboboxRef = useTemplateRef<HTMLDivElement>("repoCombobox");

const { isConnected, isLoadingStatus } = useGitHubAuth();
const {
  repos,
  isLoading: isLoadingRepos,
  error: reposError,
  refresh: refreshRepos,
  clear: clearRepos,
} = useGitHubRepos({ autoLoad: false });
const {
  bookmarks,
  isLoading: isLoadingBookmarks,
  error: bookmarksError,
  refresh: refreshBookmarks,
} = useGitHubBookmarks();

watch(
  isConnected,
  (connected) => {
    if (connected) {
      void refreshRepos();
      return;
    }

    clearRepos();
  },
  { immediate: true },
);

const bookmarkedRepoFullNames = computed(() => new Set(bookmarks.value.map((bookmark) => bookmark.fullName)));
const repoSelectorGroups = computed<readonly RepoSelectorGroup[]>(() => {
  if (repos.value.length === 0) {
    return [];
  }

  const bookmarkedRepos = repos.value.filter((repo) => bookmarkedRepoFullNames.value.has(repo.full_name));
  const otherRepos = repos.value.filter((repo) => !bookmarkedRepoFullNames.value.has(repo.full_name));
  const groups: RepoSelectorGroup[] = [];

  if (bookmarkedRepos.length > 0) {
    groups.push({ label: "Bookmarked", repos: bookmarkedRepos });
  }

  if (otherRepos.length > 0) {
    groups.push({ label: bookmarkedRepos.length > 0 ? "All repositories" : "Repositories", repos: otherRepos });
  }

  return groups;
});
const normalizedRepoFilterQuery = computed(() => repoFilterQuery.value.trim().toLowerCase());
const filteredRepoSelectorGroups = computed<readonly RepoSelectorGroup[]>(() => {
  if (!normalizedRepoFilterQuery.value) {
    return repoSelectorGroups.value;
  }

  return repoSelectorGroups.value
    .map((group) => ({
      label: group.label,
      repos: group.repos.filter((repo) => repo.full_name.toLowerCase().includes(normalizedRepoFilterQuery.value)),
    }))
    .filter((group) => group.repos.length > 0);
});
const isRepoSelectorDisabled = computed(() => !isConnected.value || repoSelectorGroups.value.length === 0);
const repoComboboxPlaceholder = computed(() => {
  if (!isConnected.value) {
    return "Connect GitHub to load repositories";
  }

  if (isLoadingRepos.value && repoSelectorGroups.value.length === 0) {
    return "Loading repositories…";
  }

  if (repoSelectorGroups.value.length === 0) {
    return "No repositories loaded";
  }

  return selectedRepoFullName.value || "Filter repositories";
});

watch(
  [repos, bookmarks],
  ([nextRepos, nextBookmarks]) => {
    if (nextRepos.length === 0) {
      selectedRepoFullName.value = "";
      return;
    }

    if (nextRepos.some((repo) => repo.full_name === selectedRepoFullName.value)) {
      return;
    }

    const bookmarkedRepo = nextBookmarks.find((bookmark) => nextRepos.some((repo) => repo.full_name === bookmark.fullName));
    selectedRepoFullName.value = bookmarkedRepo?.fullName ?? nextRepos[0]?.full_name ?? "";
  },
  { immediate: true },
);

watch(isRepoSelectorDisabled, (disabled) => {
  if (disabled) {
    isRepoComboboxOpen.value = false;
    repoFilterQuery.value = "";
  }
});

function splitRepoFullName(fullName: string): { owner: string | null; repo: string | null } {
  if (!fullName) {
    return { owner: null, repo: null };
  }

  const [owner, ...repoParts] = fullName.split("/");
  const repo = repoParts.join("/");
  return {
    owner: owner || null,
    repo: repo || null,
  };
}

const selectedRepoParts = computed(() => splitRepoFullName(selectedRepoFullName.value));
const selectedOwner = computed(() => selectedRepoParts.value.owner);
const selectedRepo = computed(() => selectedRepoParts.value.repo);
const normalizedSearchQuery = computed(() => searchQuery.value.trim().toLowerCase());

const issuesFilter = computed(() => ({
  state: "open" as const,
  sort: "updated" as const,
  direction: "desc" as const,
  search: activeTab.value === "issues" ? searchQuery.value.trim() : "",
}));

const pullsFilter = computed(() => ({
  state: "open" as const,
  sort: "updated" as const,
  direction: "desc" as const,
}));

const {
  issues,
  isLoading: isLoadingIssues,
  isSearching: isSearchingIssues,
  error: issuesError,
  hasMore: issuesHasMore,
  loadMore: loadMoreIssues,
  refetch: refetchIssues,
} = useGitHubIssues({
  owner: selectedOwner,
  repo: selectedRepo,
  filter: issuesFilter,
});

const {
  pulls,
  isLoading: isLoadingPulls,
  error: pullsError,
  hasMore: pullsHasMore,
  loadMore: loadMorePulls,
  refetch: refetchPulls,
} = useGitHubPulls({
  owner: selectedOwner,
  repo: selectedRepo,
  filter: pullsFilter,
});

const issueItems = computed<readonly GitHubIssuePanelItem[]>(() => {
  const repoFullName = selectedRepoFullName.value;

  return issues.value.map((issue: GitHubIssue) => ({
    id: issue.id,
    number: issue.number,
    title: issue.title,
    state: issue.state,
    repoFullName,
    labels: issue.labels.map((label) => ({ name: label.name, color: label.color })),
    user: {
      login: issue.user.login,
      avatarUrl: issue.user.avatar_url,
    },
    updatedAt: issue.updated_at,
    htmlUrl: issue.html_url,
  }));
});

const pullRequestItems = computed<readonly GitHubPullRequestPanelItem[]>(() => {
  const repoFullName = selectedRepoFullName.value;

  return pulls.value.map((pullRequest: GitHubPullRequest) => ({
    id: pullRequest.id,
    number: pullRequest.number,
    title: pullRequest.title,
    state: pullRequest.state,
    draft: pullRequest.draft,
    repoFullName,
    labels: pullRequest.labels.map((label) => ({ name: label.name, color: label.color })),
    user: {
      login: pullRequest.user.login,
      avatarUrl: pullRequest.user.avatar_url,
    },
    updatedAt: pullRequest.updated_at,
    htmlUrl: pullRequest.html_url,
  }));
});

const filteredPullRequests = computed(() => {
  if (!normalizedSearchQuery.value) {
    return pullRequestItems.value;
  }

  return pullRequestItems.value.filter((pullRequest) => {
    const searchableContent = [
      pullRequest.title,
      pullRequest.repoFullName,
      pullRequest.user.login,
      `#${pullRequest.number}`,
      pullRequest.draft ? "draft" : "",
      ...pullRequest.labels.map((label) => label.name),
    ].join(" ").toLowerCase();

    return searchableContent.includes(normalizedSearchQuery.value);
  });
});

const connectedLabel = computed(() => {
  if (isLoadingStatus.value) {
    return "Checking";
  }

  return isConnected.value ? "Connected" : "Disconnected";
});

const connectedPillClassName = computed(() => {
  if (isLoadingStatus.value) {
    return "connected-pill connected-pill--checking";
  }

  return isConnected.value
    ? "connected-pill connected-pill--connected"
    : "connected-pill connected-pill--disconnected";
});

const currentTabLabel = computed(() => (activeTab.value === "issues" ? "issues" : "pull requests"));
const currentTabLoading = computed(() => {
  if (activeTab.value === "issues") {
    return isLoadingIssues.value || isSearchingIssues.value;
  }

  return isLoadingPulls.value;
});
const currentTabError = computed(() => (activeTab.value === "issues" ? issuesError.value : pullsError.value));
const currentTabHasMore = computed(() => (activeTab.value === "issues" ? issuesHasMore.value : pullsHasMore.value));
const currentTabIsEmpty = computed(() => {
  if (activeTab.value === "issues") {
    return issueItems.value.length === 0;
  }

  return filteredPullRequests.value.length === 0;
});

function handleTabSelect(tabId: GitHubTab): void {
  activeTab.value = tabId;
}

function handleOpenSettings(): void {
  sidebarStore.setActiveRail("settings");
  void router.navigate({
    to: "/settings/plugins/$pluginId",
    params: { pluginId: "github" },
  });
}

function retryCurrentState(): void {
  if (reposError.value) {
    void refreshRepos(true);
    return;
  }

  if (bookmarksError.value) {
    void refreshBookmarks();
    return;
  }

  if (activeTab.value === "issues") {
    refetchIssues();
    return;
  }

  refetchPulls();
}

function loadMoreForCurrentTab(): void {
  if (activeTab.value === "issues") {
    loadMoreIssues();
    return;
  }

  loadMorePulls();
}

function handleRepoComboboxFocusIn(): void {
  if (isRepoSelectorDisabled.value) {
    return;
  }

  isRepoComboboxOpen.value = true;
}

function handleRepoComboboxFocusOut(event: FocusEvent): void {
  const nextFocusedElement = event.relatedTarget;
  if (nextFocusedElement instanceof Node && repoComboboxRef.value?.contains(nextFocusedElement)) {
    return;
  }

  isRepoComboboxOpen.value = false;
}

function handleRepoSelect(repoFullName: string): void {
  selectedRepoFullName.value = repoFullName;
  repoFilterQuery.value = "";
  isRepoComboboxOpen.value = false;
}
</script>

<template>
  <section
    class="github-panel"
    aria-label="GitHub context panel"
  >
    <header class="plugin-header">
      <Github
        class="plugin-header-icon"
        :size="16"
        aria-hidden="true"
      />
      <h2 class="plugin-header-title">
        GitHub
      </h2>
      <span :class="connectedPillClassName">
        {{ connectedLabel }}
      </span>
      <button
        type="button"
        class="plugin-settings-button"
        aria-label="Open GitHub settings"
        @click="handleOpenSettings"
      >
        <Settings
          :size="14"
          aria-hidden="true"
        />
      </button>
    </header>

    <div class="repo-selector-shell">
      <label
        class="repo-selector-label"
        for="github-repo"
      >Repository</label>
      <div
        ref="repoCombobox"
        class="repo-combobox"
        @focusin="handleRepoComboboxFocusIn"
        @focusout="handleRepoComboboxFocusOut"
      >
        <input
          id="github-repo"
          v-model="repoFilterQuery"
          class="repo-selector-input"
          type="text"
          role="combobox"
          aria-autocomplete="list"
          aria-controls="github-repo-options"
          :aria-expanded="isRepoComboboxOpen"
          :disabled="isRepoSelectorDisabled"
          :placeholder="repoComboboxPlaceholder"
          @click="isRepoComboboxOpen = true"
        >

        <div
          v-if="isRepoComboboxOpen && !isRepoSelectorDisabled"
          id="github-repo-options"
          class="repo-selector-dropdown"
          role="listbox"
          aria-label="Repositories"
        >
          <template v-if="filteredRepoSelectorGroups.length > 0">
            <div
              v-for="group in filteredRepoSelectorGroups"
              :key="group.label"
              class="repo-selector-group"
            >
              <p class="repo-selector-group-label">
                {{ group.label }}
              </p>

              <button
                v-for="repo in group.repos"
                :key="repo.id"
                type="button"
                class="repo-selector-option"
                :class="{ 'repo-selector-option--selected': repo.full_name === selectedRepoFullName }"
                role="option"
                :aria-selected="repo.full_name === selectedRepoFullName"
                @click="handleRepoSelect(repo.full_name)"
              >
                {{ repo.full_name }}
              </button>
            </div>
          </template>

          <p
            v-else
            class="repo-selector-empty-state"
          >
            No repositories match "{{ repoFilterQuery.trim() }}".
          </p>
        </div>
      </div>

      <p
        v-if="selectedRepoFullName"
        class="repo-selector-message"
      >
        Selected: {{ selectedRepoFullName }}
      </p>

      <p
        v-if="reposError && repos.length > 0"
        class="repo-selector-message repo-selector-message--error"
      >
        {{ reposError }}
      </p>
      <p
        v-else-if="bookmarksError"
        class="repo-selector-message"
      >
        Bookmarks are unavailable right now.
      </p>
      <p
        v-else-if="isLoadingBookmarks"
        class="repo-selector-message"
      >
        Loading bookmarks…
      </p>
    </div>

    <nav
      class="plugin-tabs"
      aria-label="GitHub tabs"
    >
      <button
        v-for="tab in panelTabs"
        :key="tab.id"
        type="button"
        class="plugin-tab"
        :class="{ active: activeTab === tab.id }"
        @click="handleTabSelect(tab.id)"
      >
        {{ tab.label }}
      </button>
    </nav>

    <div class="plugin-search">
      <Search
        class="plugin-search__icon"
        :size="14"
        aria-hidden="true"
      />
      <input
        v-model="searchQuery"
        class="plugin-search__input"
        type="search"
        :placeholder="`Search ${currentTabLabel}`"
        aria-label="Search GitHub panel items"
        :disabled="!isConnected || !selectedRepoFullName"
      >
    </div>

    <div
      v-if="isLoadingStatus"
      class="plugin-message-state"
    >
      Checking GitHub connection…
    </div>

    <div
      v-else-if="!isConnected"
      class="plugin-message-state plugin-message-state--empty"
    >
      <p class="plugin-message-copy">
        Connect GitHub in settings to load issues and pull requests.
      </p>
      <button
        type="button"
        class="plugin-action-button"
        @click="handleOpenSettings"
      >
        Open settings
      </button>
    </div>

    <div
      v-else-if="reposError && repos.length === 0"
      class="plugin-message-state plugin-message-state--error"
    >
      <p class="plugin-message-copy">
        {{ reposError }}
      </p>
      <button
        type="button"
        class="plugin-action-button"
        @click="retryCurrentState"
      >
        Try again
      </button>
    </div>

    <div
      v-else-if="isLoadingRepos && repos.length === 0"
      class="plugin-message-state"
    >
      Loading repositories…
    </div>

    <div
      v-else-if="repos.length === 0"
      class="plugin-message-state plugin-message-state--empty"
    >
      <p class="plugin-message-copy">
        No repositories are cached yet. Refresh repos in GitHub settings to load them.
      </p>
      <button
        type="button"
        class="plugin-action-button"
        @click="handleOpenSettings"
      >
        Open settings
      </button>
    </div>

    <div
      v-else-if="!selectedRepoFullName"
      class="plugin-message-state plugin-message-state--empty"
    >
      <p class="plugin-message-copy">
        Select a repository to view issues and pull requests.
      </p>
    </div>

    <div
      v-else-if="currentTabError && currentTabIsEmpty"
      class="plugin-message-state plugin-message-state--error"
    >
      <p class="plugin-message-copy">
        {{ currentTabError }}
      </p>
      <button
        type="button"
        class="plugin-action-button"
        @click="retryCurrentState"
      >
        Retry
      </button>
    </div>

    <div
      v-else-if="currentTabLoading && currentTabIsEmpty"
      class="plugin-message-state"
    >
      Loading {{ currentTabLabel }}…
    </div>

    <div
      v-else
      class="plugin-list"
      :data-tab="activeTab"
    >
      <div
        v-if="currentTabError"
        class="plugin-banner plugin-banner--error"
      >
        <span>{{ currentTabError }}</span>
        <button
          type="button"
          class="plugin-banner-button"
          @click="retryCurrentState"
        >
          <RefreshCw
            :size="12"
            aria-hidden="true"
          />
          Retry
        </button>
      </div>

      <template v-if="activeTab === 'issues'">
        <IssueItem
          v-for="issue in issueItems"
          :key="issue.id"
          :item="issue"
        />

        <p
          v-if="issueItems.length === 0"
          class="plugin-empty-state"
        >
          No issues found for {{ selectedRepoFullName }}.
        </p>
      </template>

      <template v-else>
        <PullRequestItem
          v-for="pullRequest in filteredPullRequests"
          :key="pullRequest.id"
          :item="pullRequest"
        />

        <p
          v-if="filteredPullRequests.length === 0"
          class="plugin-empty-state"
        >
          {{ normalizedSearchQuery ? "No pull requests match the current search." : `No pull requests found for ${selectedRepoFullName}.` }}
        </p>
      </template>

      <div
        v-if="currentTabHasMore"
        class="plugin-footer"
      >
        <button
          type="button"
          class="plugin-action-button"
          :disabled="currentTabLoading"
          @click="loadMoreForCurrentTab"
        >
          {{ currentTabLoading ? "Loading…" : "Load more" }}
        </button>
      </div>
    </div>
  </section>
</template>

<style scoped>
.github-panel {
  display: flex;
  flex: 1;
  min-height: 0;
  flex-direction: column;
  background: var(--panel-bg);
}

.plugin-header {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 14px 16px 10px;
}

.plugin-header-icon {
  color: var(--text);
}

.plugin-header-title {
  margin: 0;
  font-size: 16px;
  font-weight: 700;
}

.connected-pill {
  display: inline-flex;
  align-items: center;
  padding: 2px 8px;
  border-radius: 10px;
  font-size: 10px;
  font-weight: 600;
}

.connected-pill--connected {
  background: rgba(34, 197, 94, 0.15);
  color: #22c55e;
}

.connected-pill--disconnected {
  background: rgba(161, 161, 170, 0.15);
  color: #a1a1aa;
}

.connected-pill--checking {
  background: rgba(148, 163, 184, 0.15);
  color: #94a3b8;
}

.plugin-settings-button {
  margin-left: auto;
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 24px;
  height: 24px;
  border: 0;
  border-radius: 6px;
  background: transparent;
  color: var(--muted);
}

.plugin-settings-button:hover,
.plugin-settings-button:focus-visible {
  background: rgba(255, 255, 255, 0.05);
  color: var(--text);
  outline: none;
}

.repo-selector-shell {
  display: flex;
  flex-direction: column;
  gap: 6px;
  padding: 0 12px 12px;
}

.repo-selector-label {
  font-size: 11px;
  font-weight: 600;
  color: var(--muted);
}

.repo-selector-input {
  min-height: 34px;
  width: 100%;
  border: 1px solid var(--border);
  border-radius: var(--radius-btn);
  background: var(--card-bg);
  color: var(--text);
  padding: 0 10px;
  outline: none;
}

.repo-selector-input:focus {
  border-color: var(--accent);
}

.repo-selector-input:disabled {
  opacity: 0.7;
  cursor: not-allowed;
}

.repo-combobox {
  position: relative;
}

.repo-selector-dropdown {
  position: absolute;
  top: calc(100% + 4px);
  right: 0;
  left: 0;
  z-index: 10;
  display: flex;
  max-height: 240px;
  flex-direction: column;
  gap: 8px;
  overflow-y: auto;
  border: 1px solid var(--border);
  border-radius: var(--radius-btn);
  background: var(--card-bg);
  box-shadow: 0 8px 24px rgba(0, 0, 0, 0.22);
  padding: 8px;
}

.repo-selector-group {
  display: flex;
  flex-direction: column;
  gap: 4px;
}

.repo-selector-group-label {
  margin: 0;
  padding: 0 4px;
  font-size: 10px;
  font-weight: 700;
  color: var(--muted);
  text-transform: uppercase;
}

.repo-selector-option {
  width: 100%;
  border: 0;
  border-radius: 8px;
  background: transparent;
  color: var(--text);
  cursor: pointer;
  font-size: 12px;
  padding: 8px 10px;
  text-align: left;
}

.repo-selector-option:hover,
.repo-selector-option:focus-visible,
.repo-selector-option--selected {
  background: rgba(255, 255, 255, 0.08);
  outline: none;
}

.repo-selector-empty-state {
  margin: 0;
  font-size: 11px;
  color: var(--muted);
  padding: 4px 2px;
}

.repo-selector-message {
  margin: 0;
  font-size: 11px;
  color: var(--muted);
}

.repo-selector-message--error {
  color: #fca5a5;
}

.plugin-tabs {
  display: flex;
  border-bottom: 1px solid var(--border);
}

.plugin-tab {
  flex: 1;
  border: 0;
  border-bottom: 2px solid transparent;
  background: transparent;
  color: var(--muted);
  cursor: pointer;
  font-size: 12px;
  font-weight: 500;
  padding: 10px 0;
  text-align: center;
}

.plugin-tab.active {
  color: var(--text);
  border-bottom-color: var(--accent);
}

.plugin-search {
  position: relative;
  padding: 12px;
  border-bottom: 1px solid var(--border);
}

.plugin-search__icon {
  position: absolute;
  left: 22px;
  top: 50%;
  transform: translateY(-50%);
  color: var(--muted);
}

.plugin-search__input {
  width: 100%;
  min-height: 32px;
  padding: 7px 10px 7px 30px;
  border: 1px solid var(--border);
  border-radius: var(--radius-btn);
  background: var(--card-bg);
  color: var(--text);
  outline: none;
}

.plugin-search__input:focus {
  border-color: var(--accent);
}

.plugin-search__input:disabled {
  opacity: 0.7;
  cursor: not-allowed;
}

.plugin-message-state,
.plugin-empty-state {
  display: flex;
  flex-direction: column;
  align-items: flex-start;
  gap: 10px;
  padding: 14px 12px;
  font-size: 12px;
  color: var(--muted);
}

.plugin-message-state--error {
  color: #fca5a5;
}

.plugin-message-state--empty {
  color: var(--muted);
}

.plugin-message-copy {
  margin: 0;
}

.plugin-action-button,
.plugin-banner-button {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  gap: 6px;
  min-height: 28px;
  border: 1px solid var(--border);
  border-radius: var(--radius-btn);
  background: var(--card-bg);
  color: var(--text);
  cursor: pointer;
  font-size: 11px;
  font-weight: 600;
  padding: 0 10px;
}

.plugin-action-button:disabled,
.plugin-banner-button:disabled {
  opacity: 0.7;
  cursor: not-allowed;
}

.plugin-list {
  flex: 1;
  min-height: 0;
  overflow-y: auto;
}

.plugin-banner {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
  border-bottom: 1px solid var(--border);
  padding: 10px 12px;
  font-size: 11px;
}

.plugin-banner--error {
  background: rgba(239, 68, 68, 0.08);
  color: #fca5a5;
}

.plugin-footer {
  display: flex;
  justify-content: center;
  padding: 12px;
  border-top: 1px solid var(--border);
}
</style>
