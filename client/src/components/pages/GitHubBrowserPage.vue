<script setup lang="ts">
import { computed, shallowRef, watch } from "vue";
import { ChevronRight, Github, Loader2, Lock, Plus, Star } from "lucide-vue-next";
import { useRouter } from "@tanstack/vue-router";
import { useGitHubAuth } from "@/plugins/builtin/github/composables/use-github-auth";
import { useGitHubRepos } from "@/plugins/builtin/github/composables/use-github-repos";
import { useGitHubBookmarks } from "@/plugins/builtin/github/composables/use-github-bookmarks";
import type { BookmarkedRepo } from "@/plugins/builtin/github/composables/github-types";
import {
  Dialog,
  DialogContent,
  DialogHeader,
  DialogTitle,
  DialogTrigger,
} from "@/components/ui/dialog";
import {
  Command,
  CommandInput,
  CommandList,
  CommandItem,
  CommandEmpty,
  CommandGroup,
} from "@/components/ui/command";
import { Button } from "@/components/ui/button";

// ─── Auth ─────────────────────────────────────────────────────────────────────
const { isConnected, isLoadingStatus } = useGitHubAuth();

// ─── Repos & Bookmarks ────────────────────────────────────────────────────────
const { repos, isLoading: isLoadingRepos, refresh: refreshRepos } = useGitHubRepos({ autoLoad: false });
const { bookmarks, addBookmark, hasBookmark } = useGitHubBookmarks();

// ─── Router ───────────────────────────────────────────────────────────────────
const router = useRouter();

function navigateToRepo(repo: BookmarkedRepo) {
  void router.navigate({ to: "/github/$owner/$repo", params: { owner: repo.owner, repo: repo.name } });
}

function openGitHubSettings() {
  void router.navigate({ to: "/settings/plugins/$pluginId", params: { pluginId: "github" } });
}

// ─── Add Repository Dialog ────────────────────────────────────────────────────
const isDialogOpen = shallowRef(false);

const availableRepos = computed(() =>
  repos.value.filter((r) => !hasBookmark(r.full_name)),
);

watch(isDialogOpen, (open) => {
  if (open) {
    void refreshRepos();
  }
});

async function handleSelectRepo(repo: { full_name: string; name: string; owner_login: string }) {
  await addBookmark({
    fullName: repo.full_name,
    owner: repo.owner_login,
    name: repo.name,
  });
  isDialogOpen.value = false;
}
</script>

<template>
  <div class="github-browser">
    <header class="browser-header">
      <div class="header-text">
        <h1 class="browser-title">
          GitHub
        </h1>
        <p class="browser-subtitle">
          <span
            class="status-dot"
            :class="{
              'status-dot--connected': !isLoadingStatus && isConnected,
              'status-dot--disconnected': !isLoadingStatus && !isConnected,
            }"
            aria-hidden="true"
          />
          <template v-if="isLoadingStatus">
            Checking the connection…
          </template>
          <template v-else-if="isConnected">
            Connected. Issues and pull requests for the repositories you follow.
          </template>
          <template v-else>
            Not connected
          </template>
        </p>
      </div>
      <Dialog
        v-if="isConnected"
        v-model:open="isDialogOpen"
      >
        <DialogTrigger as-child>
          <Button size="sm">
            <Plus :size="14" />
            Add repository
          </Button>
        </DialogTrigger>
        <DialogContent class="add-repo-dialog-content">
          <DialogHeader>
            <DialogTitle>Add repository</DialogTitle>
          </DialogHeader>
          <Command>
            <CommandInput placeholder="Search repositories…" />
            <CommandList>
              <CommandEmpty>
                <span
                  v-if="isLoadingRepos"
                  class="dialog-loading"
                >
                  <Loader2
                    :size="14"
                    class="animate-spin"
                  />
                  Loading repositories…
                </span>
                <span v-else>No repositories found.</span>
              </CommandEmpty>
              <CommandGroup>
                <CommandItem
                  v-for="repo in availableRepos"
                  :key="repo.id"
                  :value="repo.full_name"
                  @select="handleSelectRepo(repo)"
                >
                  <div class="repo-item">
                    <Github :size="14" />
                    <span class="repo-item-name">{{ repo.full_name }}</span>
                    <Lock
                      v-if="repo.private"
                      :size="12"
                      class="repo-item-lock"
                    />
                    <span
                      v-if="repo.language"
                      class="repo-item-lang"
                    >{{ repo.language }}</span>
                    <span
                      v-if="repo.stargazers_count > 0"
                      class="repo-item-stars"
                    >
                      <Star :size="10" />
                      {{ repo.stargazers_count }}
                    </span>
                  </div>
                </CommandItem>
              </CommandGroup>
            </CommandList>
          </Command>
        </DialogContent>
      </Dialog>
    </header>

    <!-- Not connected -->
    <div
      v-if="!isLoadingStatus && !isConnected"
      class="browser-empty"
    >
      <Github
        :size="28"
        class="empty-icon"
        aria-hidden="true"
      />
      <p class="empty-title">
        Connect GitHub to browse issues and pull requests
      </p>
      <p class="empty-subtitle">
        Fleet uses it to list your repositories and start sessions from an issue or pull request.
      </p>
      <Button
        size="sm"
        class="empty-action"
        @click="openGitHubSettings"
      >
        Connect in Settings
      </Button>
    </div>

    <template v-else-if="isConnected">
      <div
        v-if="bookmarks.length > 0"
        class="repo-grid"
      >
        <div
          v-for="repo in bookmarks"
          :key="repo.fullName"
          class="repo-card"
          role="button"
          tabindex="0"
          @click="navigateToRepo(repo)"
          @keydown.enter.prevent="navigateToRepo(repo)"
        >
          <span class="repo-card__head">
            <Github
              :size="16"
              class="repo-card__icon"
              aria-hidden="true"
            />
            <span class="repo-card__name">
              <span class="repo-card__owner">{{ repo.owner }} /</span>
              {{ repo.name }}
            </span>
            <ChevronRight
              :size="14"
              class="repo-card__chevron"
              aria-hidden="true"
            />
          </span>
          <span class="repo-card__url">github.com/{{ repo.fullName }}</span>
        </div>
      </div>

      <div
        v-else
        class="browser-empty"
      >
        <p class="empty-title">
          No repositories yet
        </p>
        <p class="empty-subtitle">
          Add a repository to see its issues and pull requests here.
        </p>
      </div>
    </template>
  </div>
</template>

<style scoped>
.github-browser {
  display: flex;
  flex-direction: column;
  gap: 20px;
  height: 100%;
  overflow: visible;
}

/* ─── Header ──────────────────────────────────────────────────────────────── */
.browser-header {
  display: flex;
  align-items: flex-end;
  gap: 12px;
  flex-shrink: 0;
}

.header-text {
  flex: 1;
  min-width: 0;
}

.browser-title {
  margin: 0;
  color: var(--text);
  font-size: 22px;
  font-weight: 600;
  letter-spacing: -0.01em;
  line-height: 1.2;
}

.browser-subtitle {
  display: flex;
  align-items: center;
  gap: 8px;
  margin: 4px 0 0;
  color: var(--muted);
  font-size: 13px;
}

.status-dot {
  width: 7px;
  height: 7px;
  flex-shrink: 0;
  border-radius: 50%;
  background: var(--muted);
}

.status-dot--connected {
  background: var(--running);
}

.status-dot--disconnected {
  background: var(--error);
}

/* ─── Repo grid ───────────────────────────────────────────────────────────── */
.repo-grid {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(260px, 1fr));
  gap: 12px;
}

.repo-card {
  display: flex;
  flex-direction: column;
  gap: 4px;
  padding: 12px 14px;
  border: 1px solid var(--border);
  border-radius: var(--radius-card);
  background: var(--card-bg);
  color: var(--text);
  cursor: pointer;
  transition: border-color var(--transition), background-color var(--transition);
}

.repo-card:hover {
  border-color: color-mix(in srgb, var(--text) 18%, transparent);
}

.repo-card:focus-visible {
  outline: 2px solid var(--accent);
  outline-offset: 1px;
}

.repo-card__head {
  display: flex;
  align-items: center;
  gap: 8px;
  min-width: 0;
}

.repo-card__icon,
.repo-card__chevron {
  flex-shrink: 0;
  color: var(--muted);
}

.repo-card__name {
  flex: 1;
  min-width: 0;
  overflow: hidden;
  font-size: 14px;
  font-weight: 600;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.repo-card__owner {
  color: var(--muted);
  font-weight: 500;
}

.repo-card__url {
  overflow: hidden;
  padding-left: 24px;
  color: var(--muted);
  font-size: 12px;
  text-overflow: ellipsis;
  white-space: nowrap;
}

/* ─── Dialog items ────────────────────────────────────────────────────────── */
.dialog-loading {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  color: var(--muted);
}

.repo-item {
  display: flex;
  align-items: center;
  gap: 8px;
  width: 100%;
}

.repo-item-name {
  flex: 1;
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.repo-item-lock {
  color: var(--muted);
  flex-shrink: 0;
}

.repo-item-lang {
  font-size: 11px;
  padding: 1px 6px;
  border-radius: 999px;
  background: color-mix(in srgb, var(--text) 7%, transparent);
  color: var(--muted);
  flex-shrink: 0;
}

.repo-item-stars {
  display: inline-flex;
  align-items: center;
  gap: 2px;
  font-size: 11px;
  color: var(--muted);
  flex-shrink: 0;
}

/* ─── Empty/disconnected ──────────────────────────────────────────────────── */
.browser-empty {
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  gap: 6px;
  padding: 56px 24px;
  border: 1px dashed var(--border);
  border-radius: var(--radius-card);
  text-align: center;
}

.empty-icon {
  margin-bottom: 6px;
  color: var(--muted);
}

.empty-title {
  margin: 0;
  color: var(--text);
  font-size: 15px;
  font-weight: 600;
}

.empty-subtitle {
  max-width: 420px;
  margin: 0;
  color: var(--muted);
  font-size: 13px;
  line-height: 1.5;
}

.empty-action {
  margin-top: 10px;
}
</style>
