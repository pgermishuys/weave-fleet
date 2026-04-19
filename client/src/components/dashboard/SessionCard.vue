<script setup lang="ts">
import { computed, shallowRef } from "vue";
import { Archive, RotateCcw, Square, Trash2 } from "lucide-vue-next";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card, CardContent } from "@/components/ui/card";
import {
  useArchiveSession,
  useDeleteSession,
  useTerminateSession,
  useUnarchiveSession,
} from "@/composables/use-session-actions";
import type { SessionListItem } from "@/lib/api-types";
import { sessionCache } from "@/lib/session-cache";
import { dispatchSessionRemoved } from "@/lib/session-sync";
import { useSessionsStore } from "@/stores/sessions";
import ConfirmDeleteSessionDialog from "@/components/sessions/ConfirmDeleteSessionDialog.vue";

interface Props {
  session: SessionListItem;
}

interface Emits {
  select: [session: SessionListItem];
  changed: [];
}

const props = defineProps<Props>();
const emit = defineEmits<Emits>();
const sessionsStore = useSessionsStore();

const { archiveSession, isArchiving } = useArchiveSession();
const { unarchiveSession, isUnarchiving } = useUnarchiveSession();
const { terminateSession, isTerminating } = useTerminateSession();
const { deleteSession, isDeleting } = useDeleteSession();
const isDeleteDialogOpen = shallowRef(false);

const sessionId = computed(() => props.session.session.id);
const instanceId = computed(() => props.session.instanceId);
const displayTitle = computed(() => props.session.session.title?.trim() || "Untitled session");
const isArchived = computed(() => props.session.retentionStatus === "archived");
const isRunning = computed(() => props.session.lifecycleStatus === "running");
const canArchive = computed(() => !isArchived.value && !isRunning.value);
const canUnarchive = computed(() => isArchived.value);
const canTerminate = computed(() => isRunning.value);
const isPending = computed(() => {
  return isArchiving.value || isDeleting.value || isTerminating.value || isUnarchiving.value;
});

const statusLabel = computed(() => {
  switch (props.session.sessionStatus) {
    case "completed":
      return "Completed";
    case "idle":
      return "Idle";
    case "stopped":
      return "Stopped";
    case "disconnected":
      return "Disconnected";
    case "error":
      return "Error";
    case "waiting_input":
      return "Queued";
    case "active":
    default:
      return "Active";
  }
});

const statusClasses = computed(() => {
  switch (props.session.sessionStatus) {
    case "completed":
      return "bg-emerald-500";
    case "idle":
      return "bg-amber-500";
    case "stopped":
    case "disconnected":
      return "bg-slate-400";
    case "error":
      return "bg-rose-500";
    case "waiting_input":
      return "bg-violet-500";
    case "active":
    default:
      return "bg-sky-500";
  }
});

const sessionMeta = computed(() => {
  return props.session.workspaceDisplayName?.trim() || props.session.workspaceDirectory;
});

function handleSelect(): void {
  emit("select", props.session);
}

async function handleArchive(): Promise<void> {
  try {
    await archiveSession(sessionId.value);
    sessionsStore.patchSession(sessionId.value, { retentionStatus: "archived" });
    emit("changed");
  } catch {
    // Mutation state is owned by the composable.
  }
}

async function handleUnarchive(): Promise<void> {
  try {
    await unarchiveSession(sessionId.value);
    sessionsStore.patchSession(sessionId.value, { retentionStatus: "active" });
    emit("changed");
  } catch {
    // Mutation state is owned by the composable.
  }
}

async function handleTerminate(): Promise<void> {
  try {
    await terminateSession(sessionId.value, instanceId.value);
    sessionsStore.patchSession(sessionId.value, {
      activityStatus: "idle",
      lifecycleStatus: "stopped",
      sessionStatus: "stopped",
    });
    emit("changed");
  } catch {
    // Mutation state is owned by the composable.
  }
}

async function handleDelete(): Promise<void> {
  try {
    await deleteSession(sessionId.value, instanceId.value);
    isDeleteDialogOpen.value = false;
    sessionCache.delete(sessionId.value, instanceId.value);
    dispatchSessionRemoved(sessionId.value);
    sessionsStore.removeSession(sessionId.value);
    emit("changed");
  } catch {
    // Mutation state is owned by the composable.
  }
}

function openDeleteDialog(): void {
  isDeleteDialogOpen.value = true;
}
</script>

<template>
  <Card
    :data-session-id="sessionId"
    data-testid="session-card"
    class="group relative cursor-pointer border-border/70 bg-card/80 py-0 transition hover:border-primary/40 hover:shadow-md focus-within:border-primary/50"
    role="button"
    tabindex="0"
    @click="handleSelect"
    @keydown.enter.prevent="handleSelect"
    @keydown.space.prevent="handleSelect"
  >
    <CardContent class="px-5 py-5">
      <div class="flex items-start justify-between gap-4">
        <div class="min-w-0 flex-1 space-y-3">
          <div class="flex items-center gap-3">
            <span
              data-testid="session-card-status-indicator"
              :class="statusClasses"
              class="h-2.5 w-2.5 shrink-0 rounded-full"
            />
            <p class="text-sm font-medium text-muted-foreground">
              {{ statusLabel }}
            </p>
            <Badge v-if="isArchived" data-testid="session-card-archived-badge" variant="secondary">
              Archived
            </Badge>
          </div>

          <div class="space-y-1">
            <h3 data-testid="session-title" class="truncate text-lg font-semibold tracking-tight text-foreground">
              {{ displayTitle }}
            </h3>
            <p class="truncate text-sm text-muted-foreground">
              {{ sessionMeta }}
            </p>
          </div>
        </div>

        <div class="flex shrink-0 items-center gap-2 opacity-0 transition group-hover:opacity-100 group-focus-within:opacity-100">
          <Button
            v-if="canTerminate"
            data-testid="session-terminate-button"
            variant="outline"
            size="icon-sm"
            :disabled="isPending"
            aria-label="Terminate session"
            @click.stop="void handleTerminate()"
          >
            <Square class="h-4 w-4" />
          </Button>

          <Button
            v-if="canArchive"
            data-testid="session-archive-button"
            variant="outline"
            size="icon-sm"
            :disabled="isPending"
            aria-label="Archive session"
            @click.stop="void handleArchive()"
          >
            <Archive class="h-4 w-4" />
          </Button>

          <Button
            v-if="canUnarchive"
            data-testid="session-unarchive-button"
            variant="outline"
            size="icon-sm"
            :disabled="isPending"
            aria-label="Unarchive session"
            @click.stop="void handleUnarchive()"
          >
            <RotateCcw class="h-4 w-4" />
          </Button>

          <Button
            data-testid="session-delete-button"
            variant="outline"
            size="icon-sm"
            :disabled="isPending"
            aria-label="Delete session"
            @click.stop="openDeleteDialog"
          >
            <Trash2 class="h-4 w-4" />
          </Button>
        </div>
      </div>
    </CardContent>
  </Card>

  <ConfirmDeleteSessionDialog
    v-model:open="isDeleteDialogOpen"
    :is-deleting="isDeleting"
    :session-title="displayTitle"
    @confirm="void handleDelete()"
  />
</template>
