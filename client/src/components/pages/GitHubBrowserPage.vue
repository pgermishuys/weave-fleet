<script setup lang="ts">
import { computed, shallowRef, watch } from "vue";
import { Github, Loader2, Plus, Star, Lock } from "lucide-vue-next";
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
    <!-- Header -->
    <div class="browser-header">
      <div class="header-text">
        <h1 class="browser-title">
          GitHub
        </h1>
        <p class="browser-subtitle">
          Browse issues and pull requests for your repositories
        </p>
      </div>
      <span
        v-if="isLoadingStatus"
        class="status-pill status-pill--loading"
      >Checking…</span>
      <span
        v-else-if="isConnected"
        class="status-pill status-pill--connected"
      >Connected</span>
      <span
        v-else
        class="status-pill status-pill--disconnected"
      >Disconnected</span>
    </div>

    <!-- Not connected -->
    <div
      v-if="!isLoadingStatus && !isConnected"
      class="browser-empty"
    >
      <p class="empty-title">
        GitHub is not connected.
      </p>
      <p class="empty-subtitle">
        Connect GitHub in Settings to browse repositories.
      </p>
    </div>

    <template v-else-if="isConnected">
      <!-- Add Repository button + dialog -->
      <div class="actions-bar">
        <Dialog v-model:open="isDialogOpen">
          <DialogTrigger as-child>
            <button class="add-repo-btn">
              <Plus :size="14" />
              Add Repository
            </button>
          </DialogTrigger>
          <DialogContent class="add-repo-dialog-content">
            <DialogHeader>
              <DialogTitle>Add Repository</DialogTitle>
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
      </div>

      <!-- Repo cards grid -->
      <div
        v-if="bookmarks.length > 0"
        class="repo-grid"
      >
        <button
          v-for="repo in bookmarks"
          :key="repo.fullName"
          class="repo-card"
          @click="navigateToRepo(repo)"
        >
          <Github :size="16" />
          <span>{{ repo.fullName }}</span>
        </button>
      </div>

      <!-- Empty state -->
      <div
        v-else
        class="browser-empty"
      >
        <p class="empty-title">
          No repositories added yet.
        </p>
        <p class="empty-subtitle">
          Click 'Add Repository' to get started.
        </p>
      </div>
    </template>
  </div>
</template>

<style scoped>
.github-browser {
  display: flex;
  flex-direction: column;
  height: 100%;
  overflow: visible;
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

.header-text {
  flex: 1;
}

.browser-title {
  font-size: 14px;
  font-weight: 600;
  color: var(--text);
  margin: 0;
}

.browser-subtitle {
  font-size: 11px;
  color: var(--muted);
  margin: 2px 0 0;
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

/* ─── Actions bar ─────────────────────────────────────────────────────────── */
.actions-bar {
  display: flex;
  padding: 12px 16px;
  flex-shrink: 0;
}

.add-repo-btn {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  padding: 6px 12px;
  border: 1px solid var(--border);
  border-radius: 6px;
  background: transparent;
  color: var(--text);
  font-size: 12px;
  cursor: pointer;
}

.add-repo-btn:hover {
  background: var(--sidebar-item-hover);
}

/* ─── Repo grid ───────────────────────────────────────────────────────────── */
.repo-grid {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(280px, 1fr));
  gap: 12px;
  padding: 16px;
}

.repo-card {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 16px;
  border: 1px solid var(--border);
  border-radius: 8px;
  background: var(--card, var(--sidebar));
  cursor: pointer;
  transition: background 0.15s;
  text-align: left;
  color: var(--text);
  font-size: 13px;
}

.repo-card:hover {
  background: var(--accent, rgba(255, 255, 255, 0.05));
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
  font-size: 10px;
  padding: 1px 6px;
  border-radius: 999px;
  background: var(--sidebar-item-hover);
  color: var(--muted);
  flex-shrink: 0;
}

.repo-item-stars {
  display: inline-flex;
  align-items: center;
  gap: 2px;
  font-size: 10px;
  color: var(--muted);
  flex-shrink: 0;
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
