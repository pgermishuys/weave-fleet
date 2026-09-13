<script setup lang="ts">
import { computed } from "vue";
import { Check, ChevronDown, Folder, GitBranch, GitFork, History, LoaderCircle } from "lucide-vue-next";
import { DropdownMenuItem, DropdownMenuSeparator } from "reka-ui";
import type { WorktreeInfo } from "@/api/client";
import { DropdownMenu, DropdownMenuContent, DropdownMenuTrigger } from "@/components/ui/dropdown-menu";
import { tildePath } from "@/lib/new-session-plan";
import type { NewSessionWorkspace } from "@/lib/new-session-request";

const props = defineProps<{
  repositoryPath: string;
  workspace: NewSessionWorkspace;
  currentBranch: string | null;
  worktrees: readonly WorktreeInfo[];
  isLoadingWorktrees: boolean;
  lastWorktreePath: string | null;
  disabled?: boolean;
}>();

const emit = defineEmits<{
  "update:workspace": [workspace: NewSessionWorkspace];
  closeAutoFocus: [event: Event];
}>();

const open = defineModel<boolean>("open", { default: false });

/** A worktree next to the repository as `…-worktrees/x`, anything else as a ~ path. */
function worktreeLocation(path: string): string {
  return path.startsWith(props.repositoryPath) ? `…${path.slice(props.repositoryPath.length)}` : tildePath(path);
}

function folderName(path: string): string {
  return path.split(/[/\\]/).filter(Boolean).pop() ?? path;
}

/** Existing worktrees, the one used last first. */
const orderedWorktrees = computed(() => {
  return [...props.worktrees].sort((left, right) => {
    if (left.path === props.lastWorktreePath) return -1;
    if (right.path === props.lastWorktreePath) return 1;
    return 0;
  });
});

const selectedWorktree = computed(() => {
  const workspace = props.workspace;
  return workspace.kind === "existing"
    ? props.worktrees.find((worktree) => worktree.path === workspace.path) ?? null
    : null;
});

const chipLabel = computed(() => {
  const workspace = props.workspace;
  if (workspace.kind === "current") return "Current checkout";
  if (workspace.kind === "new") return "New worktree";
  return selectedWorktree.value?.branch ?? folderName(workspace.path);
});

function choose(workspace: NewSessionWorkspace): void {
  emit("update:workspace", workspace);
}
</script>

<template>
  <DropdownMenu
    v-model:open="open"
    :modal="false"
  >
    <DropdownMenuTrigger as-child>
      <button
        type="button"
        class="ns-chip"
        data-testid="new-session-workspace-chip"
        :disabled="disabled"
      >
        <Folder
          v-if="workspace.kind === 'current'"
          class="ns-chip__icon"
          aria-hidden="true"
        />
        <GitFork
          v-else-if="workspace.kind === 'new'"
          class="ns-chip__icon"
          aria-hidden="true"
        />
        <GitBranch
          v-else
          class="ns-chip__icon"
          aria-hidden="true"
        />
        <span class="ns-chip__label">
          {{ chipLabel }}<span
            v-if="workspace.kind === 'current' && currentBranch"
            class="ns-chip__hint"
          > · {{ currentBranch }}</span>
        </span>
        <ChevronDown
          class="ns-chip__chevron"
          aria-hidden="true"
        />
      </button>
    </DropdownMenuTrigger>

    <DropdownMenuContent
      class="ns-pop"
      side="top"
      align="start"
      :side-offset="6"
      :collision-padding="8"
      @close-auto-focus="emit('closeAutoFocus', $event)"
    >
      <div class="ns-pop__label">
        Workspace
      </div>
      <DropdownMenuItem
        class="ns-option"
        data-testid="new-session-workspace-current"
        @select="choose({ kind: 'current' })"
      >
        <Folder
          class="ns-option__icon"
          aria-hidden="true"
        />
        <span class="ns-option__text">
          <span class="ns-option__title">Current checkout</span>
          <span class="ns-option__detail ns-option__detail--mono">
            {{ tildePath(repositoryPath) }}{{ currentBranch ? ` · ${currentBranch}` : "" }}
          </span>
        </span>
        <Check
          v-if="workspace.kind === 'current'"
          class="ns-option__check"
          aria-hidden="true"
        />
      </DropdownMenuItem>
      <DropdownMenuItem
        class="ns-option"
        data-testid="new-session-workspace-new"
        @select="choose({ kind: 'new' })"
      >
        <GitFork
          class="ns-option__icon"
          aria-hidden="true"
        />
        <span class="ns-option__text">
          <span class="ns-option__title">New worktree</span>
          <span class="ns-option__detail">New branch from the default branch</span>
        </span>
        <Check
          v-if="workspace.kind === 'new'"
          class="ns-option__check"
          aria-hidden="true"
        />
      </DropdownMenuItem>

      <template v-if="orderedWorktrees.length > 0 || isLoadingWorktrees">
        <DropdownMenuSeparator class="ns-pop__separator" />
        <div class="ns-pop__label">
          Existing worktrees
        </div>
        <div
          v-if="isLoadingWorktrees && orderedWorktrees.length === 0"
          class="ns-pop__note ns-workspace-loading"
        >
          <LoaderCircle
            class="ns-option__icon animate-spin"
            aria-hidden="true"
          />
          Loading worktrees…
        </div>
        <DropdownMenuItem
          v-for="worktree in orderedWorktrees"
          :key="worktree.path"
          class="ns-option"
          @select="choose({ kind: 'existing', path: worktree.path })"
        >
          <History
            v-if="worktree.path === lastWorktreePath"
            class="ns-option__icon"
            aria-hidden="true"
          />
          <GitBranch
            v-else
            class="ns-option__icon"
            aria-hidden="true"
          />
          <span class="ns-option__text">
            <span class="ns-option__title">
              {{ worktree.branch ?? folderName(worktree.path) }}<span
                v-if="worktree.path === lastWorktreePath"
                class="ns-option__tag"
              > · last used</span>
            </span>
            <span
              class="ns-option__detail ns-option__detail--mono"
              :title="worktree.path"
            >{{ worktreeLocation(worktree.path) }}</span>
          </span>
          <Check
            v-if="workspace.kind === 'existing' && workspace.path === worktree.path"
            class="ns-option__check"
            aria-hidden="true"
          />
        </DropdownMenuItem>
      </template>
    </DropdownMenuContent>
  </DropdownMenu>
</template>

<style scoped>
.ns-workspace-loading {
  display: flex;
  align-items: center;
  gap: 8px;
}
</style>
