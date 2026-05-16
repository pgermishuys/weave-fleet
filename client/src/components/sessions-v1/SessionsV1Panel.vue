<script setup lang="ts">
import { computed, shallowRef, useTemplateRef } from "vue";
import { useRouter } from "@tanstack/vue-router";
import { AlertTriangle, Eye, EyeOff, Loader2, Plus, Trash2 } from "lucide-vue-next";
import type { SessionListItem } from "@/lib/api-types";
import { useSessionsV1 } from "@/composables/use-sessions-v1";
import { useWorkspaces } from "@/composables/use-workspaces";
import { usePersistedState } from "@/composables/use-persisted-state";
import { useDeleteSessionV1 } from "@/composables/use-session-actions-v1";
import { useSessionsV1Store } from "@/stores/sessions-v1";
import { storeToRefs } from "pinia";
import NewSessionDialog from "@/components/sessions/NewSessionDialog.vue";
import WorkspaceGroup from "./WorkspaceGroup.vue";

const HIDE_INACTIVE_KEY = "weave:sidebar:hideInactive";
const isNewSessionOpen = shallowRef(false);
const router = useRouter();

function isInactiveSession(s: SessionListItem): boolean {
  return s.lifecycleStatus === "stopped"
    || s.lifecycleStatus === "completed"
    || s.lifecycleStatus === "disconnected";
}

const sessionsStore = useSessionsV1Store();
const { retentionStatus } = storeToRefs(sessionsStore);

const { sessions, isLoading, error, refetch } = useSessionsV1({ retentionStatus });
const workspaces = useWorkspaces(sessions);
const { deleteSession } = useDeleteSessionV1();

const [hideInactive, setHideInactive] = usePersistedState<boolean>(HIDE_INACTIVE_KEY, false);
const isRemovingInactive = shallowRef(false);
const showDeleteConfirm = shallowRef(false);
const treeRef = useTemplateRef<HTMLElement>("tree");

const inactiveSessions = computed(() => sessions.value.filter(isInactiveSession));

const visibleWorkspaces = computed(() => {
  if (!hideInactive.value) {
    return workspaces.value;
  }

  return workspaces.value.filter((g) => g.sessions.some((s) => !isInactiveSession(s)));
});

async function removeAllInactive(): Promise<void> {
  isRemovingInactive.value = true;
  showDeleteConfirm.value = false;

  try {
    await Promise.allSettled(
      inactiveSessions.value.map((s) => deleteSession(s.session.id, s.instanceId)),
    );
    await refetch();
  } finally {
    isRemovingInactive.value = false;
  }
}

function handleKeyDown(event: KeyboardEvent): void {
  const tree = treeRef.value;

  if (!tree) {
    return;
  }

  const items = Array.from(
    tree.querySelectorAll<HTMLElement>("[role='treeitem'], [data-tree-leaf]"),
  );
  const focused = document.activeElement as HTMLElement | null;
  const currentIndex = focused ? items.indexOf(focused) : -1;

  switch (event.key) {
    case "ArrowDown": {
      event.preventDefault();
      items[currentIndex + 1]?.focus();
      break;
    }
    case "ArrowUp": {
      event.preventDefault();
      items[currentIndex - 1]?.focus();
      break;
    }
    case "ArrowRight": {
      event.preventDefault();

      if (focused?.getAttribute("role") === "treeitem") {
        const isCollapsed = focused.dataset.collapsed === "true";

        if (isCollapsed) {
          focused.click();
        } else {
          items[currentIndex + 1]?.focus();
        }
      }

      break;
    }
    case "ArrowLeft": {
      event.preventDefault();

      if (focused?.dataset.treeLeaf !== undefined) {
        for (let i = currentIndex - 1; i >= 0; i--) {
          if (items[i]?.getAttribute("role") === "treeitem") {
            items[i]?.focus();
            break;
          }
        }
      } else if (focused?.getAttribute("role") === "treeitem") {
        const isCollapsed = focused.dataset.collapsed === "true";

        if (!isCollapsed) {
          focused.click();
        }
      }

      break;
    }
    case "Enter": {
      event.preventDefault();
      focused?.click();
      break;
    }
    case "F2": {
      event.preventDefault();

      if (focused?.getAttribute("role") === "treeitem") {
        (focused.querySelector<HTMLElement>("[data-rename-trigger]"))?.dispatchEvent(new MouseEvent("dblclick"));
      }

      break;
    }
  }
}
</script>

<template>
  <section
    class="v1-panel"
    aria-label="Sessions V1"
  >
    <!-- Header row -->
    <div class="panel-header">
      <button
        type="button"
        class="panel-title"
        @click="router.navigate({ to: '/sessions-v1' })"
      >
        Sessions
      </button>

      <div class="header-actions">
        <!-- Hide/show inactive -->
        <button
          type="button"
          class="header-btn"
          :title="hideInactive ? 'Show inactive sessions' : 'Hide inactive sessions'"
          :aria-pressed="hideInactive"
          @click="setHideInactive(!hideInactive)"
        >
          <EyeOff
            v-if="hideInactive"
            :size="13"
          />
          <Eye
            v-else
            :size="13"
          />
        </button>

        <!-- Delete inactive -->
        <button
          v-if="inactiveSessions.length > 0"
          type="button"
          class="header-btn header-btn--danger"
          :title="`Remove ${inactiveSessions.length} inactive session${inactiveSessions.length !== 1 ? 's' : ''}`"
          :disabled="isRemovingInactive"
          @click="showDeleteConfirm = true"
        >
          <Loader2
            v-if="isRemovingInactive"
            :size="13"
            class="spin"
          />
          <Trash2
            v-else
            :size="13"
          />
        </button>

        <!-- New session -->
        <button
          type="button"
          class="header-btn"
          title="New Session"
          @click="isNewSessionOpen = true"
        >
          <Plus :size="13" />
        </button>
        <NewSessionDialog
          v-model:open="isNewSessionOpen"
          create-endpoint="/api/sessions-v1"
        />
      </div>
    </div>

    <!-- Error state -->
    <div
      v-if="error"
      class="error-state"
    >
      <AlertTriangle
        :size="13"
        class="error-icon"
      />
      <span>Failed to load sessions</span>
    </div>

    <!-- Loading state -->
    <div
      v-else-if="isLoading && sessions.length === 0"
      class="loading-state"
    >
      <Loader2
        :size="14"
        class="spin"
      />
    </div>

    <!-- Empty state -->
    <p
      v-else-if="visibleWorkspaces.length === 0"
      class="empty-state"
    >
      {{ hideInactive && workspaces.length > 0 ? 'No active sessions' : 'No workspaces yet' }}
    </p>

    <!-- Workspace tree -->
    <nav
      v-else
      ref="tree"
      role="tree"
      class="workspace-tree"
      aria-label="Workspaces"
      @keydown="handleKeyDown"
    >
      <WorkspaceGroup
        v-for="group in visibleWorkspaces"
        :key="group.workspaceId"
        :group="group"
        :hide-inactive="hideInactive"
        :refetch="refetch"
      />
    </nav>

    <!-- Delete inactive confirmation -->
    <div
      v-if="showDeleteConfirm"
      class="confirm-overlay"
      role="dialog"
      aria-modal="true"
      aria-label="Confirm delete inactive sessions"
    >
      <div class="confirm-dialog">
        <p class="confirm-title">
          Remove Inactive Sessions
        </p>
        <p class="confirm-body">
          This will permanently delete {{ inactiveSessions.length }}
          inactive session{{ inactiveSessions.length !== 1 ? 's' : '' }}.
          This action cannot be undone.
        </p>
        <div class="confirm-actions">
          <button
            type="button"
            class="confirm-btn confirm-btn--cancel"
            @click="showDeleteConfirm = false"
          >
            Cancel
          </button>
          <button
            type="button"
            class="confirm-btn confirm-btn--danger"
            @click="removeAllInactive"
          >
            Delete
          </button>
        </div>
      </div>
    </div>
  </section>
</template>

<style scoped>
.v1-panel {
  display: flex;
  flex: 1;
  flex-direction: column;
  min-height: 0;
  background: var(--panel-bg);
  position: relative;
}

.panel-header {
  display: flex;
  align-items: center;
  padding: 10px 12px 8px;
  gap: 4px;
  border-bottom: 1px solid var(--border);
}

.panel-title {
  flex: 1;
  font-size: 10px;
  font-weight: 600;
  text-transform: uppercase;
  letter-spacing: 0.05em;
  color: var(--muted);
  background: none;
  border: none;
  padding: 0;
  cursor: pointer;
  text-align: left;
}

.panel-title:hover {
  color: var(--text);
}

.header-actions {
  display: flex;
  align-items: center;
  gap: 2px;
}

.header-btn {
  display: flex;
  align-items: center;
  justify-content: center;
  width: 24px;
  height: 24px;
  background: transparent;
  border: none;
  border-radius: var(--radius-btn);
  color: var(--muted);
  cursor: pointer;
  transition: color 0.1s ease, background-color 0.1s ease;
  padding: 0;
}

.header-btn:hover {
  background: rgba(255, 255, 255, 0.06);
  color: var(--text);
}

.header-btn--danger:hover {
  color: #ef4444;
}

.workspace-tree {
  flex: 1;
  overflow-y: auto;
  padding: 4px 6px;
  display: flex;
  flex-direction: column;
  gap: 1px;
}

.error-state {
  display: flex;
  align-items: center;
  gap: 6px;
  padding: 12px 16px;
  font-size: 11px;
  color: #ef4444;
}

.loading-state {
  display: flex;
  align-items: center;
  justify-content: center;
  padding: 24px;
  color: var(--muted);
}

.empty-state {
  padding: 12px 16px;
  font-size: 11px;
  color: var(--muted);
  margin: 0;
}

.spin {
  animation: spin 1s linear infinite;
}

@keyframes spin {
  from { transform: rotate(0deg); }
  to { transform: rotate(360deg); }
}

.confirm-overlay {
  position: absolute;
  inset: 0;
  background: rgba(0, 0, 0, 0.5);
  display: flex;
  align-items: center;
  justify-content: center;
  z-index: 50;
}

.confirm-dialog {
  background: var(--panel-bg);
  border: 1px solid var(--border);
  border-radius: var(--radius-card);
  padding: 16px;
  max-width: 240px;
  display: flex;
  flex-direction: column;
  gap: 8px;
}

.confirm-title {
  font-size: 12px;
  font-weight: 600;
  color: var(--text);
  margin: 0;
}

.confirm-body {
  font-size: 11px;
  color: var(--muted);
  margin: 0;
  line-height: 1.5;
}

.confirm-actions {
  display: flex;
  gap: 6px;
  justify-content: flex-end;
  margin-top: 4px;
}

.confirm-btn {
  height: 26px;
  padding: 0 10px;
  border-radius: var(--radius-btn);
  font-size: 11px;
  cursor: pointer;
  border: 1px solid var(--border);
  transition: background-color 0.1s ease;
}

.confirm-btn--cancel {
  background: transparent;
  color: var(--muted);
}

.confirm-btn--cancel:hover {
  background: rgba(255, 255, 255, 0.06);
}

.confirm-btn--danger {
  background: #ef4444;
  color: #fff;
  border-color: #ef4444;
}

.confirm-btn--danger:hover {
  background: #dc2626;
}
</style>
