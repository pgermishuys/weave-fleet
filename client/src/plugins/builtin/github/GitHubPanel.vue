<script setup lang="ts">
import { computed, onMounted, shallowRef, watch } from "vue";
import { useLocation, useRouter } from "@tanstack/vue-router";
import { FolderGit2, Inbox, Plus, Settings, X } from "lucide-vue-next";
import AddRepositoryDialog from "@/components/github/AddRepositoryDialog.vue";
import ReviewerAvatar from "@/components/github/ReviewerAvatar.vue";
import { useGitHubWork } from "@/composables/use-github-work";
import { needsYou } from "@/lib/github-items";
import { useGitHubBookmarks } from "./composables/use-github-bookmarks";
import { useGitHubAuth } from "./composables/use-github-auth";

const router = useRouter();
const pathname = useLocation({ select: (location) => location.pathname });
const { isConnected } = useGitHubAuth();
const { bookmarks, removeBookmark } = useGitHubBookmarks();
const { work, load, refresh } = useGitHubWork();

const isAddOpen = shallowRef(false);

onMounted(() => {
  if (isConnected.value) void load();
});
watch(isConnected, (connected) => {
  if (connected) void load();
});

const needsCount = computed(() => (work.value ? needsYou(work.value).length : 0));
const counts = computed(() => new Map((work.value?.repos ?? []).map((repo) => [repo.fullName.toLowerCase(), repo])));
const onHome = computed(() => pathname.value === "/github" || pathname.value === "/github/");

function isCurrent(fullName: string): boolean {
  const path = pathname.value.toLowerCase();
  const repoPath = `/github/${fullName.toLowerCase()}`;
  return path === repoPath || path.startsWith(`${repoPath}/`);
}

function countsFor(fullName: string): string | null {
  const repo = counts.value.get(fullName.toLowerCase());
  return repo ? `${repo.openPullRequests} · ${repo.openIssues}` : null;
}

function navigateToGitHub(): void {
  void router.navigate({ to: "/github" as string });
}

function navigateToRepo(bookmark: { fullName: string; owner: string; name: string }): void {
  void router.navigate({ to: `/github/${bookmark.owner}/${bookmark.name}` as string });
}

function openSettings(): void {
  void router.navigate({ to: "/settings/plugins/$pluginId", params: { pluginId: "github" } });
}

async function handleRemove(fullName: string): Promise<void> {
  await removeBookmark(fullName);
  void refresh();
}
</script>

<template>
  <div class="github-panel">
    <div class="panel-account">
      <ReviewerAvatar
        v-if="work?.login"
        :login="work.login"
        :avatar-url="work.avatarUrl"
        :size="24"
      />
      <span
        v-else
        class="panel-account__dot-only"
        aria-hidden="true"
      />
      <span class="panel-account__who">
        <span class="panel-account__name">{{ work?.login ?? "GitHub" }}</span>
        <span
          class="panel-account__status"
          :data-connected="isConnected"
        >{{ isConnected ? "Connected" : "Not connected" }}</span>
      </span>
      <button
        type="button"
        class="panel-icon-btn"
        title="GitHub settings"
        aria-label="GitHub settings"
        @click="openSettings"
      >
        <Settings :size="13" />
      </button>
    </div>

    <button
      type="button"
      class="panel-row"
      :class="{ 'panel-row--current': onHome }"
      data-testid="github-panel-home"
      @click="navigateToGitHub"
    >
      <Inbox
        :size="14"
        class="panel-row__icon"
        aria-hidden="true"
      />
      <span class="panel-row__name">Your work</span>
      <span
        v-if="needsCount"
        class="panel-row__count panel-row__count--hot"
        :title="`${needsCount} need you`"
      >{{ needsCount }}</span>
    </button>

    <template v-if="isConnected">
      <p class="panel-heading">
        Repositories
      </p>

      <p
        v-if="bookmarks.length === 0"
        class="panel-empty"
      >
        Follow a repository to see its pull requests and issues here.
      </p>

      <div
        v-for="bookmark in bookmarks"
        :key="bookmark.fullName"
        class="bookmark-item"
      >
        <button
          type="button"
          class="panel-row bookmark-link"
          :class="{ 'panel-row--current': isCurrent(bookmark.fullName) }"
          :title="bookmark.fullName"
          @click="navigateToRepo(bookmark)"
        >
          <FolderGit2
            :size="14"
            class="panel-row__icon"
            aria-hidden="true"
          />
          <span class="panel-row__name">
            <span class="panel-row__owner">{{ bookmark.owner }}/</span>{{ bookmark.name }}
          </span>
          <span
            v-if="countsFor(bookmark.fullName)"
            class="panel-row__count"
            title="Open pull requests · open issues"
          >{{ countsFor(bookmark.fullName) }}</span>
        </button>
        <button
          type="button"
          class="panel-icon-btn bookmark-remove"
          :title="`Stop following ${bookmark.fullName}`"
          :aria-label="`Stop following ${bookmark.fullName}`"
          @click.stop="handleRemove(bookmark.fullName)"
        >
          <X :size="11" />
        </button>
      </div>

      <button
        type="button"
        class="panel-row panel-row--quiet"
        data-testid="github-panel-add"
        @click="isAddOpen = true"
      >
        <Plus
          :size="14"
          class="panel-row__icon"
          aria-hidden="true"
        />
        <span class="panel-row__name">Follow a repository</span>
      </button>

      <AddRepositoryDialog
        v-model:open="isAddOpen"
        @added="refresh"
      />
    </template>
  </div>
</template>

<style scoped>
.github-panel {
  display: flex;
  flex-direction: column;
  gap: 1px;
  height: 100%;
  padding: 4px 6px;
}

.panel-account {
  display: flex;
  align-items: center;
  gap: 9px;
  margin-bottom: 6px;
  padding: 6px 6px 10px;
  border-bottom: 1px solid var(--border);
}

.panel-account__dot-only {
  width: 24px;
  height: 24px;
  border-radius: 50%;
  background: color-mix(in srgb, var(--text) 10%, transparent);
}

.panel-account__who {
  display: grid;
  flex: 1;
  min-width: 0;
}

.panel-account__name {
  overflow: hidden;
  color: var(--text);
  font-size: 12.5px;
  font-weight: 600;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.panel-account__status {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  color: var(--muted);
  font-size: 11px;
}

.panel-account__status::before {
  content: "";
  width: 6px;
  height: 6px;
  border-radius: 50%;
  background: var(--error);
}

.panel-account__status[data-connected="true"]::before {
  background: var(--running);
}

.panel-heading {
  margin: 12px 8px 4px;
  color: color-mix(in srgb, var(--muted) 80%, transparent);
  font-size: 11px;
  font-weight: 600;
  letter-spacing: 0.06em;
  text-transform: uppercase;
}

.panel-row {
  display: flex;
  align-items: center;
  gap: 8px;
  width: 100%;
  min-width: 0;
  min-height: 32px;
  padding: 0 8px;
  border: 0;
  border-radius: var(--radius-btn);
  background: transparent;
  color: color-mix(in srgb, var(--text) 86%, transparent);
  font-size: 13px;
  text-align: left;
  cursor: pointer;
  transition: background-color var(--transition), color var(--transition);
}

.panel-row:hover {
  background: color-mix(in srgb, var(--text) 5%, transparent);
  color: var(--text);
}

.panel-row--current {
  background: color-mix(in srgb, var(--text) 9%, transparent);
  color: var(--text);
  font-weight: 500;
}

.panel-row--quiet {
  color: var(--muted);
}

.panel-row:focus-visible,
.panel-icon-btn:focus-visible {
  outline: 2px solid var(--accent);
  outline-offset: -2px;
}

.panel-row__icon {
  flex-shrink: 0;
  color: var(--muted);
}

.panel-row__name {
  flex: 1;
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.panel-row__owner {
  color: var(--muted);
}

.panel-row__count {
  flex-shrink: 0;
  color: var(--muted);
  font-family: var(--font-mono-stack);
  font-size: 11px;
  font-variant-numeric: tabular-nums;
}

.panel-row__count--hot {
  color: var(--pr-blocked);
}

.panel-empty {
  margin: 0;
  padding: 4px 8px 8px;
  color: var(--muted);
  font-size: 12px;
}

.bookmark-item {
  position: relative;
}

.panel-icon-btn {
  display: inline-grid;
  place-items: center;
  width: 22px;
  height: 22px;
  flex-shrink: 0;
  border: 0;
  border-radius: 5px;
  background: transparent;
  color: var(--muted);
  cursor: pointer;
}

.panel-icon-btn:hover {
  background: color-mix(in srgb, var(--text) 8%, transparent);
  color: var(--text);
}

/* Stop following takes the counts' place while the row is hovered. */
.bookmark-remove {
  position: absolute;
  top: 50%;
  right: 5px;
  transform: translateY(-50%);
  visibility: hidden;
}

.bookmark-item:hover .bookmark-remove,
.bookmark-remove:focus-visible {
  visibility: visible;
}

.bookmark-item:hover .panel-row__count {
  visibility: hidden;
}
</style>
