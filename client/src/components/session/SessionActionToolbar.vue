<script setup lang="ts">
import { computed } from "vue";
import { Archive, ArchiveRestore, ChartNoAxesColumn, Copy, Ellipsis, FileDown, GitFork, Loader2, OctagonX, Pencil, SquareTerminal, Trash2 } from "lucide-vue-next";
import { useIsMobileNav } from "@/composables/use-media-query";
import { useAppShellStore } from "@/stores/app-shell";
import { useTerminalsStore } from "@/stores/terminals";
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
  /** For a phone's ⋯ menu, which holds the actions and the usage the header has no room for. */
  sessionId?: string | null;
  totalTokens?: number | null;
  totalCost?: number | null;
}>(), {
  canFork: true,
  forkDisabledReason: null,
  directory: null,
  sessionId: null,
  totalTokens: null,
  totalCost: null,
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
  export: [];
}>();

// On a phone the header keeps two buttons (the right panel and this menu), so the rest moves in here.
const phone = useIsMobileNav();
const appShell = useAppShellStore();
const terminals = useTerminalsStore();
const showTerminalItem = computed(() => phone.value && Boolean(props.sessionId) && appShell.config.terminalEnabled);
const terminalOpen = computed(() => Boolean(props.sessionId) && terminals.isOpen(props.sessionId as string));
const usage = computed(() => {
  const parts: string[] = [];
  if (props.totalTokens != null) parts.push(`${props.totalTokens.toLocaleString()} tokens`);
  if (props.totalCost != null) parts.push(new Intl.NumberFormat("en-US", { style: "currency", currency: "USD" }).format(props.totalCost));
  return parts.join(" · ");
});

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
      v-if="props.canAbort && !phone"
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

    <span
      v-if="!phone"
      class="session-action-toolbar__divider"
    />

    <!-- A disabled button shows no tooltip, so its reason sits on a wrapper. -->
    <span
      v-if="!phone && (props.canFork || props.forkDisabledReason)"
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
        <template v-if="phone">
          <DropdownMenuItem
            v-if="usage"
            disabled
            data-testid="session-usage-item"
            class="opacity-100 text-muted-foreground"
          >
            <ChartNoAxesColumn class="size-3.5" />
            {{ usage }}
          </DropdownMenuItem>
          <DropdownMenuItem
            v-if="showTerminalItem"
            data-testid="session-terminal-action"
            @select="terminals.toggleOpen(props.sessionId as string)"
          >
            <SquareTerminal class="size-3.5" />
            {{ terminalOpen ? "Hide terminal" : "Show terminal" }}
          </DropdownMenuItem>
          <DropdownMenuItem
            v-if="props.canFork || props.forkDisabledReason"
            data-testid="session-fork-action"
            :disabled="props.isPending || !props.hasSession || !props.canFork"
            :title="props.canFork ? undefined : props.forkDisabledReason ?? undefined"
            @select="emit('fork')"
          >
            <GitFork class="size-3.5" />
            Fork
          </DropdownMenuItem>
          <DropdownMenuSeparator />
        </template>
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
          <!-- A double-click is a pointer's gesture; on touch the hint means nothing. -->
          <DropdownMenuShortcut class="tracking-normal [@media(pointer:coarse)]:hidden">
            Double-click title
          </DropdownMenuShortcut>
        </DropdownMenuItem>
        <DropdownMenuItem
          data-testid="session-export-markdown-action"
          @select="emit('export')"
        >
          <FileDown class="size-3.5" />
          Export as Markdown…
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
