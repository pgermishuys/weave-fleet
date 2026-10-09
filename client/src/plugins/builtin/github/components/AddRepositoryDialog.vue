<script setup lang="ts">
import { computed, watch } from "vue";
import { Github, Loader2, Lock, Star } from "lucide-vue-next";
import {
  Command,
  CommandEmpty,
  CommandGroup,
  CommandInput,
  CommandItem,
  CommandList,
} from "@/components/ui/command";
import { Dialog, DialogContent, DialogHeader, DialogTitle } from "@/components/ui/dialog";
import { useGitHubBookmarks } from "@/plugins/builtin/github/composables/use-github-bookmarks";
import { useGitHubRepos } from "@/plugins/builtin/github/composables/use-github-repos";

/** Picks one of the user's GitHub repositories to follow in Fleet. */
const open = defineModel<boolean>("open", { required: true });

const emit = defineEmits<{
  added: [fullName: string];
}>();

const { repos, isLoading, refresh } = useGitHubRepos({ autoLoad: false });
const { addBookmark, hasBookmark } = useGitHubBookmarks();

const available = computed(() => repos.value.filter((repo) => !hasBookmark(repo.full_name)));

watch(open, (isOpen) => {
  if (isOpen) void refresh();
});

async function add(repo: { full_name: string; name: string; owner_login: string }): Promise<void> {
  await addBookmark({ fullName: repo.full_name, owner: repo.owner_login, name: repo.name });
  open.value = false;
  emit("added", repo.full_name);
}
</script>

<template>
  <Dialog v-model:open="open">
    <DialogContent>
      <DialogHeader>
        <DialogTitle>Follow a repository</DialogTitle>
      </DialogHeader>
      <Command>
        <CommandInput placeholder="Search your repositories…" />
        <CommandList>
          <CommandEmpty>
            <span
              v-if="isLoading"
              class="add-repo__loading"
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
              v-for="repo in available"
              :key="repo.id"
              :value="repo.full_name"
              @select="add(repo)"
            >
              <div class="add-repo__item">
                <Github :size="14" />
                <span class="add-repo__name">{{ repo.full_name }}</span>
                <Lock
                  v-if="repo.private"
                  :size="12"
                  class="add-repo__muted"
                />
                <span
                  v-if="repo.language"
                  class="add-repo__lang"
                >{{ repo.language }}</span>
                <span
                  v-if="repo.stargazers_count > 0"
                  class="add-repo__stars"
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
</template>

<style scoped>
.add-repo__loading {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  color: var(--muted);
}

.add-repo__item {
  display: flex;
  align-items: center;
  gap: 8px;
  width: 100%;
}

.add-repo__name {
  flex: 1;
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.add-repo__muted {
  flex-shrink: 0;
  color: var(--muted);
}

.add-repo__lang {
  flex-shrink: 0;
  padding: 1px 6px;
  border-radius: 999px;
  background: color-mix(in srgb, var(--text) 7%, transparent);
  color: var(--muted);
  font-size: 11px;
}

.add-repo__stars {
  display: inline-flex;
  flex-shrink: 0;
  align-items: center;
  gap: 2px;
  color: var(--muted);
  font-size: 11px;
}
</style>
