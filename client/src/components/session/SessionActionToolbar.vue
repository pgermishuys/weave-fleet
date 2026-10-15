<script setup lang="ts">
import { Archive, ArchiveRestore, Copy, Ellipsis, GitFork, Loader2, OctagonX, Pencil, Trash2 } from "lucide-vue-next";
import { Button } from "@/components/ui/button";
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuSeparator,
  DropdownMenuShortcut,
  DropdownMenuTrigger,
} from "@/components/ui/dropdown-menu";

const props = withDefaults(defineProps<{
  canAbort?: boolean;
  canArchive?: boolean;
  canRestore?: boolean;
  canFork?: boolean;
  /** Why Fork is off; given, the button shows disabled with it instead of going away. */
  forkDisabledReason?: string | null;
  canDelete?: boolean;
  canRename?: boolean;
  isPending?: boolean;
  isAborting?: boolean;
  isRenaming?: boolean;
  isDeleting?: boolean;
  isArchiving?: boolean;
  hasSession?: boolean;
  hasInstance?: boolean;
  /** The session's working folder (the worktree in a worktree session): Copy folder path copies it. */
  directory?: string | null;
  errors?: readonly string[];
}>(), {
  canFork: true,
  forkDisabledReason: null,
  directory: null,
  canDelete: true,
  canRename: true,
});

const emit = defineEmits<{
  abort: [];
  fork: [];
  rename: [];
  delete: [];
  archive: [];
  restore: [];
}>();

async function copyDirectory(): Promise<void> {
  if (!props.directory) return;
  try {
    await navigator.clipboard.writeText(props.directory);
  } catch {
    // Clipboard failures are non-fatal.
  }
}
</script>

<template>
  <div
    class="session-action-toolbar"
    aria-label="Session actions"
  >
    <Button
      v-if="props.canAbort"
      variant="toolbar-icon-danger"
      size="toolbar"
      data-testid="abort-button"
      :disabled="props.isPending || !props.hasSession || !props.hasInstance"
      title="Abort"
      @click="emit('abort')"
    >
      <Loader2
        v-if="props.isAborting"
        class="session-action-toolbar__spinner"
        aria-hidden="true"
      />
      <OctagonX
        v-else
        aria-hidden="true"
      />
    </Button>

    <span class="session-action-toolbar__divider" />

    <!-- A disabled button shows no tooltip, so its reason sits on a wrapper. -->
    <span
      v-if="props.canFork || props.forkDisabledReason"
      class="session-action-toolbar__fork"
      :title="props.canFork ? 'Fork: a new session with a copy of this conversation' : props.forkDisabledReason ?? undefined"
      data-testid="session-fork-button-wrapper"
    >
      <Button
        variant="toolbar-icon"
        size="toolbar"
        data-testid="session-archived-fork-button"
        :disabled="props.isPending || !props.hasSession || !props.canFork"
        :aria-label="props.canFork ? 'Fork' : `Fork: ${props.forkDisabledReason}`"
        @click="emit('fork')"
      >
        <GitFork aria-hidden="true" />
      </Button>
    </span>

    <!-- Actions that are costly to hit by mistake live one click further away. -->
    <DropdownMenu :modal="false">
      <DropdownMenuTrigger as-child>
        <Button
          variant="toolbar-icon"
          size="toolbar"
          data-testid="session-more-actions"
          :disabled="!props.hasSession"
          aria-label="More actions"
          title="More actions"
        >
          <Loader2
            v-if="props.isRenaming || props.isArchiving || props.isDeleting"
            class="session-action-toolbar__spinner"
            aria-hidden="true"
          />
          <Ellipsis
            v-else
            aria-hidden="true"
          />
        </Button>
      </DropdownMenuTrigger>
      <DropdownMenuContent
        align="end"
        class="w-56"
      >
        <!-- First, for windows too narrow for the header to show the path. -->
        <DropdownMenuItem
          v-if="props.directory"
          data-testid="session-copy-directory-action"
          :title="props.directory"
          @select="copyDirectory"
        >
          <Copy class="size-3.5" />
          Copy folder path
        </DropdownMenuItem>
        <DropdownMenuItem
          v-if="props.canRename"
          data-testid="session-rename-action"
          :disabled="props.isPending"
          @select="emit('rename')"
        >
          <Pencil class="size-3.5" />
          Rename
          <DropdownMenuShortcut class="tracking-normal">
            Double-click title
          </DropdownMenuShortcut>
        </DropdownMenuItem>
        <DropdownMenuItem
          v-if="props.canArchive"
          data-testid="session-archive-banner-button"
          :disabled="props.isPending"
          @select="emit('archive')"
        >
          <Archive class="size-3.5" />
          Archive
        </DropdownMenuItem>
        <DropdownMenuItem
          v-if="props.canRestore"
          data-testid="session-restore-action"
          :disabled="props.isPending"
          @select="emit('restore')"
        >
          <ArchiveRestore class="size-3.5" />
          Restore
        </DropdownMenuItem>
        <template v-if="props.canDelete">
          <DropdownMenuSeparator />
          <DropdownMenuItem
            variant="destructive"
            data-testid="session-delete-button"
            :disabled="props.isPending || !props.hasInstance"
            @select="emit('delete')"
          >
            <Trash2 class="size-3.5" />
            Delete…
          </DropdownMenuItem>
        </template>
      </DropdownMenuContent>
    </DropdownMenu>

    <p
      v-for="message in props.errors ?? []"
      :key="message"
      class="session-action-toolbar__error"
      role="alert"
    >
      {{ message }}
    </p>
  </div>
</template>

<style scoped>
.session-action-toolbar {
  display: flex;
  align-items: center;
  gap: 2px;
  flex-wrap: wrap;
}

.session-action-toolbar__divider {
  width: 1px;
  height: 16px;
  margin-inline: 2px;
  background: var(--border);
}

.session-action-toolbar__fork {
  display: inline-flex;
}

.session-action-toolbar__spinner {
  animation: session-action-toolbar-spin 0.8s linear infinite;
}

.session-action-toolbar__error {
  width: 100%;
  margin: 2px 0 0;
  font-size: 10px;
  color: var(--error);
}

@keyframes session-action-toolbar-spin {
  from { transform: rotate(0deg); }
  to { transform: rotate(360deg); }
}
</style>
