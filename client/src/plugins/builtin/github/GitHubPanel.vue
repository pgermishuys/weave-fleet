<script setup lang="ts">
import { useRouter } from "@tanstack/vue-router";
import { Github, Plus, X } from "lucide-vue-next";
import { useGitHubBookmarks } from "./composables/use-github-bookmarks";
import { useGitHubAuth } from "./composables/use-github-auth";

const router = useRouter();
const { isConnected } = useGitHubAuth();
const { bookmarks, removeBookmark } = useGitHubBookmarks();

function navigateToGitHub() {
  void router.navigate({ to: "/github" as string });
}

function navigateToRepo(bookmark: { fullName: string; owner: string; name: string }) {
  void router.navigate({ to: `/github/${bookmark.owner}/${bookmark.name}` as string });
}

async function handleRemove(fullName: string) {
  await removeBookmark(fullName);
}
</script>

<template>
  <div class="github-panel">
    <div class="panel-header">
      <button class="panel-title-btn" @click="navigateToGitHub()">
        <Github :size="14" />
        <span>GitHub</span>
      </button>
      <button
        class="add-btn"
        title="Browse repositories"
        @click="navigateToGitHub()"
      >
        <Plus :size="13" />
      </button>
    </div>

    <div v-if="!isConnected" class="panel-disconnected">
      Not connected
    </div>

    <template v-else>
      <div v-if="bookmarks.length === 0" class="panel-empty">
        No bookmarked repos.
      </div>

      <div v-else class="bookmark-list">
        <div
          v-for="bookmark in bookmarks"
          :key="bookmark.fullName"
          class="bookmark-item"
        >
          <button
            class="bookmark-link"
            :title="bookmark.fullName"
            @click="navigateToRepo(bookmark)"
          >
            <span class="bookmark-name">{{ bookmark.fullName }}</span>
          </button>
          <button
            class="bookmark-remove"
            :title="`Remove ${bookmark.fullName}`"
            @click.stop="handleRemove(bookmark.fullName)"
          >
            <X :size="10" />
          </button>
        </div>
      </div>
    </template>
  </div>
</template>

<style scoped>
.github-panel {
  display: flex;
  flex-direction: column;
  height: 100%;
  padding: 4px 0;
}

.panel-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 4px 8px 8px;
}

.panel-title-btn {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  background: transparent;
  border: none;
  color: var(--text);
  font-size: 12px;
  font-weight: 600;
  cursor: pointer;
  padding: 2px 4px;
  border-radius: 4px;
}

.panel-title-btn:hover {
  background: var(--sidebar-item-hover);
}

.add-btn {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 22px;
  height: 22px;
  background: transparent;
  border: none;
  color: var(--muted);
  cursor: pointer;
  border-radius: 4px;
}

.add-btn:hover {
  background: var(--sidebar-item-hover);
  color: var(--text);
}

.panel-disconnected,
.panel-empty {
  padding: 8px 12px;
  font-size: 11px;
  color: var(--muted);
}

.bookmark-list {
  display: flex;
  flex-direction: column;
}

.bookmark-item {
  display: flex;
  align-items: center;
  padding: 1px 6px;
}

.bookmark-item:hover .bookmark-remove {
  opacity: 1;
}

.bookmark-link {
  flex: 1;
  display: flex;
  align-items: center;
  gap: 6px;
  padding: 4px 6px;
  background: transparent;
  border: none;
  color: var(--sidebar-item-text, var(--text));
  font-size: 12px;
  cursor: pointer;
  border-radius: 4px;
  min-width: 0;
  text-align: left;
}

.bookmark-link:hover {
  background: var(--sidebar-item-hover);
}

.bookmark-name {
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.bookmark-remove {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 18px;
  height: 18px;
  background: transparent;
  border: none;
  color: var(--muted);
  cursor: pointer;
  border-radius: 3px;
  flex-shrink: 0;
  opacity: 0;
}

.bookmark-remove:hover {
  background: var(--sidebar-item-hover);
  color: var(--text);
}
</style>
