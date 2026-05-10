<script setup lang="ts">
import { computed } from "vue";
import { useRouter } from "@tanstack/vue-router";
import { Clock, Copy, GitBranch, Loader2, OctagonX, RotateCcw, Square, Trash2, WifiOff } from "lucide-vue-next";
import {
  ContextMenu,
  ContextMenuContent,
  ContextMenuItem,
  ContextMenuSeparator,
  ContextMenuTrigger,
} from "@/components/ui/context-menu";
import type { SessionListItem } from "@/lib/api-types";
import { formatRelativeTime } from "@/lib/format-utils";
import { useDeleteSession, useResumeSession, useAbortSession, useTerminateSession } from "@/composables/use-session-actions";

interface Props {
  item: SessionListItem;
  isParent?: boolean;
  isChild?: boolean;
}

const props = defineProps<Props>();

const router = useRouter();
const { deleteSession } = useDeleteSession();
const { resumeSession, resumingSessionId } = useResumeSession();
const { abortSession } = useAbortSession();
const { terminateSession } = useTerminateSession();

const isDisconnected = computed(() => props.item.lifecycleStatus === "disconnected");
const isStopped = computed(() => props.item.lifecycleStatus === "stopped" || props.item.lifecycleStatus === "completed");
const isInactive = computed(() => isDisconnected.value || isStopped.value);

const isBusy = computed(() => props.item.activityStatus === "busy");
const isResuming = computed(() => resumingSessionId.value === props.item.session.id);

const canTerminate = computed(() => !isStopped.value);
const canAbort = computed(() => props.item.activityStatus === "busy");
const canDelete = computed(() => isInactive.value);

const sessionPath = computed(() => `/sessions/${encodeURIComponent(props.item.session.id)}?instanceId=${encodeURIComponent(props.item.instanceId)}`);

async function handleTerminate(): Promise<void> {
  await terminateSession(props.item.session.id, props.item.instanceId);
}

async function handleAbort(): Promise<void> {
  await abortSession(props.item.session.id, props.item.instanceId);
}

async function handleResume(): Promise<void> {
  await resumeSession(props.item.session.id);
}

async function handleDelete(): Promise<void> {
  await deleteSession(props.item.session.id, props.item.instanceId);
}

function navigate(): void {
  void router.navigate({ to: sessionPath.value as "/" });
}

function copyPath(): void {
  void navigator.clipboard.writeText(props.item.workspaceDirectory);
}
</script>

<template>
  <ContextMenu>
    <ContextMenuTrigger as-child>
      <div
        class="session-item"
        :class="{ 'session-item--inactive': isInactive }"
        data-tree-leaf
        tabindex="0"
        :aria-label="item.session.title || item.session.id"
        role="option"
        @click="navigate"
        @keydown.enter="navigate"
      >
        <!-- Status dot -->
        <span
          class="status-dot"
          :class="isBusy ? 'status-dot--busy' : 'status-dot--idle'"
          aria-hidden="true"
        />

        <!-- Title -->
        <span class="session-title">
          {{ item.session.title || item.session.id.slice(0, 12) }}
        </span>

        <!-- Badges -->
        <span class="badges">
          <span
            v-if="isDisconnected"
            class="badge badge--warn"
            title="Disconnected"
          >
            <WifiOff :size="9" />
          </span>
          <span
            v-else-if="isStopped"
            class="badge"
            title="Stopped"
          >
            <Square :size="9" />
          </span>
          <span
            v-if="item.isolationStrategy === 'worktree'"
            class="badge badge--purple"
            title="Worktree"
          >
            <GitBranch :size="9" />
          </span>
          <span
            v-else-if="item.isolationStrategy === 'clone'"
            class="badge badge--purple"
            title="Clone"
          >
            <Copy :size="9" />
          </span>
          <span
            v-if="isParent"
            class="badge badge--cyan"
            title="Conductor"
          >cond</span>
          <span
            v-if="isChild"
            class="badge badge--orange"
            title="Child"
          >child</span>
        </span>

        <!-- Time -->
        <span class="rel-time">
          <Clock
            :size="9"
            aria-hidden="true"
          />
          {{ formatRelativeTime(item.session.time.created) }}
        </span>

        <!-- Hover actions -->
        <span class="hover-actions">
          <button
            v-if="canAbort"
            type="button"
            class="action-btn action-btn--amber"
            title="Interrupt"
            @click.stop="handleAbort"
          >
            <OctagonX :size="11" />
          </button>
          <button
            v-if="isInactive"
            type="button"
            class="action-btn action-btn--green"
            :disabled="isResuming"
            title="Resume"
            @click.stop="handleResume"
          >
            <Loader2
              v-if="isResuming"
              :size="11"
              class="spin"
            />
            <RotateCcw
              v-else
              :size="11"
            />
          </button>
          <button
            v-if="canTerminate"
            type="button"
            class="action-btn action-btn--red"
            title="Terminate"
            @click.stop="handleTerminate"
          >
            <Trash2 :size="11" />
          </button>
          <button
            v-if="canDelete"
            type="button"
            class="action-btn action-btn--red"
            title="Delete"
            @click.stop="handleDelete"
          >
            <Trash2 :size="11" />
          </button>
        </span>
      </div>
    </ContextMenuTrigger>

    <ContextMenuContent>
      <ContextMenuItem @click="navigate">
        Open Session
      </ContextMenuItem>
      <ContextMenuSeparator />
      <ContextMenuItem @click="copyPath">
        Copy workspace path
      </ContextMenuItem>
    </ContextMenuContent>
  </ContextMenu>
</template>

<style scoped>
.session-item {
  display: flex;
  align-items: center;
  gap: 5px;
  padding: 3px 6px;
  border-radius: var(--radius-btn);
  cursor: pointer;
  min-width: 0;
  transition: background-color 0.1s ease;
}

.session-item:hover,
.session-item:focus-visible {
  background: rgba(255, 255, 255, 0.06);
  outline: none;
}

.session-item--inactive {
  opacity: 0.6;
}

.status-dot {
  width: 6px;
  height: 6px;
  border-radius: 50%;
  flex-shrink: 0;
}

.status-dot--busy {
  background: #22c55e;
  animation: pulse 2s ease-in-out infinite;
}

.status-dot--idle {
  background: rgba(161, 161, 170, 0.5);
}

@keyframes pulse {
  0%, 100% { opacity: 1; }
  50% { opacity: 0.4; }
}

.session-title {
  font-family: var(--font-mono);
  font-size: 11px;
  flex: 1;
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  color: var(--text);
}

.badges {
  display: flex;
  align-items: center;
  gap: 2px;
  flex-shrink: 0;
}

.badge {
  display: inline-flex;
  align-items: center;
  gap: 2px;
  font-size: 9px;
  padding: 1px 3px;
  border-radius: 3px;
  background: rgba(255, 255, 255, 0.08);
  color: var(--muted);
  font-family: var(--font-mono);
}

.badge--warn {
  color: #f59e0b;
}

.badge--purple {
  color: #a78bfa;
}

.badge--cyan {
  color: #22d3ee;
  background: rgba(34, 211, 238, 0.1);
}

.badge--orange {
  color: #fb923c;
  background: rgba(251, 146, 60, 0.1);
}

.rel-time {
  display: flex;
  align-items: center;
  gap: 3px;
  font-size: 9px;
  color: var(--muted);
  flex-shrink: 0;
  white-space: nowrap;
  opacity: 0.7;
}

.hover-actions {
  display: flex;
  align-items: center;
  gap: 2px;
  flex-shrink: 0;
  opacity: 0;
  transition: opacity 0.1s ease;
}

.session-item:hover .hover-actions,
.session-item:focus-visible .hover-actions {
  opacity: 1;
}

.action-btn {
  display: flex;
  align-items: center;
  justify-content: center;
  width: 18px;
  height: 18px;
  border: none;
  background: transparent;
  border-radius: 3px;
  cursor: pointer;
  color: var(--muted);
  transition: color 0.1s ease, background-color 0.1s ease;
  padding: 0;
}

.action-btn:hover {
  background: rgba(255, 255, 255, 0.08);
}

.action-btn--red:hover {
  color: #ef4444;
}

.action-btn--amber:hover {
  color: #f59e0b;
}

.action-btn--green:hover {
  color: #22c55e;
}

.spin {
  animation: spin 1s linear infinite;
}

@keyframes spin {
  from { transform: rotate(0deg); }
  to { transform: rotate(360deg); }
}
</style>
