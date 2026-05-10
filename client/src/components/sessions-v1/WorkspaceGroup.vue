<script setup lang="ts">
import { computed, shallowRef } from "vue";
import { ChevronRight, MoreHorizontal, Plus, Trash2 } from "lucide-vue-next";
import {
  Collapsible,
  CollapsibleContent,
  CollapsibleTrigger,
} from "@/components/ui/collapsible";
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuSeparator,
  DropdownMenuTrigger,
} from "@/components/ui/dropdown-menu";
import type { WorkspaceGroup } from "@/composables/use-workspaces";
import { useRenameWorkspace } from "@/composables/use-rename-workspace";
import { useTerminateSessionV1 as useTerminateSession } from "@/composables/use-session-actions-v1";
import { nestSessions } from "@/lib/session-utils";
import { usePersistedState } from "@/composables/use-persisted-state";
import InlineEdit from "@/components/sessions/InlineEdit.vue";
import WorkspaceSessionItem from "./WorkspaceSessionItem.vue";

interface Props {
  group: WorkspaceGroup;
  hideInactive?: boolean;
  onNewSession?: (directory: string) => void;
  refetch?: () => void;
}

const props = defineProps<Props>();

const COLLAPSED_KEY = "weave:fleet:collapsed";

const { renameWorkspace } = useRenameWorkspace();
const { terminateSession } = useTerminateSession();

const [collapsedIds, setCollapsedIds] = usePersistedState<string[]>(COLLAPSED_KEY, []);
const isRenaming = shallowRef(false);

const isCollapsed = computed(() => collapsedIds.value.includes(props.group.workspaceId));

const nestedSessions = computed(() => nestSessions(props.group.sessions));

function toggleCollapse(): void {
  const current = collapsedIds.value;
  const id = props.group.workspaceId;

  if (current.includes(id)) {
    setCollapsedIds(current.filter((x) => x !== id));
  } else {
    setCollapsedIds([...current, id]);
  }
}

async function handleRename(newName: string): Promise<void> {
  isRenaming.value = false;
  await renameWorkspace(props.group.workspaceId, newName, props.refetch);
}

async function handleTerminateAll(): Promise<void> {
  const active = props.group.sessions.filter(
    (s) => s.lifecycleStatus !== "stopped" && s.lifecycleStatus !== "completed" && s.lifecycleStatus !== "disconnected",
  );

  await Promise.allSettled(active.map((s) => terminateSession(s.session.id, s.instanceId)));
  props.refetch?.();
}
</script>

<template>
  <Collapsible
    :open="!isCollapsed"
    @update:open="toggleCollapse"
  >
    <!-- Group header -->
    <div
      class="group-header"
      role="treeitem"
      :data-collapsed="isCollapsed"
      tabindex="0"
      :aria-expanded="!isCollapsed"
      :aria-label="group.displayName"
    >
      <CollapsibleTrigger as-child>
        <button
          type="button"
          class="chevron-btn"
          tabindex="-1"
          aria-hidden="true"
          @click.stop
        >
          <ChevronRight
            :size="12"
            class="chevron"
            :class="{ 'chevron--open': !isCollapsed }"
          />
        </button>
      </CollapsibleTrigger>

      <!-- Inline-editable name -->
      <InlineEdit
        v-if="isRenaming"
        :initial-value="group.displayName"
        class="rename-input"
        @commit="handleRename"
        @cancel="isRenaming = false"
      />
      <span
        v-else
        class="workspace-name"
        data-rename-trigger
        @dblclick="isRenaming = true"
      >
        {{ group.displayName }}
      </span>

      <!-- Session count -->
      <span class="session-count">{{ group.sessionCount }}</span>

      <!-- Overflow menu -->
      <DropdownMenu>
        <DropdownMenuTrigger as-child>
          <button
            type="button"
            class="overflow-btn"
            title="Workspace actions"
            @click.stop
          >
            <MoreHorizontal :size="13" />
          </button>
        </DropdownMenuTrigger>
        <DropdownMenuContent align="end">
          <DropdownMenuItem
            v-if="onNewSession"
            class="menu-item"
            @click="onNewSession(group.workspaceDirectory)"
          >
            <Plus :size="12" />
            New Session
          </DropdownMenuItem>
          <DropdownMenuSeparator v-if="onNewSession" />
          <DropdownMenuItem
            class="menu-item menu-item--destructive"
            @click="handleTerminateAll"
          >
            <Trash2 :size="12" />
            Terminate All
          </DropdownMenuItem>
        </DropdownMenuContent>
      </DropdownMenu>
    </div>

    <!-- Sessions list -->
    <CollapsibleContent class="collapsible-content">
      <div
        v-if="group.sessions.length === 0"
        class="empty-state"
      >
        No sessions in this workspace.
      </div>
      <div
        v-else
        class="sessions-list"
      >
        <div
          v-for="{ item, children } in nestedSessions"
          :key="`${item.instanceId}-${item.session.id}`"
        >
          <WorkspaceSessionItem
            :item="item"
            :is-parent="children.length > 0"
          />
          <div
            v-if="children.length > 0"
            class="children-list"
          >
            <WorkspaceSessionItem
              v-for="child in children"
              :key="`${child.instanceId}-${child.session.id}`"
              :item="child"
              is-child
            />
          </div>
        </div>
      </div>
    </CollapsibleContent>
  </Collapsible>
</template>

<style scoped>
.group-header {
  display: flex;
  align-items: center;
  gap: 4px;
  padding: 4px 4px 4px 2px;
  border-radius: var(--radius-btn);
  cursor: pointer;
  transition: background-color 0.1s ease;
}

.group-header:hover,
.group-header:focus-visible {
  background: rgba(255, 255, 255, 0.05);
  outline: none;
}

.group-header:hover .overflow-btn {
  opacity: 1;
}

.chevron-btn {
  display: flex;
  align-items: center;
  justify-content: center;
  width: 16px;
  height: 16px;
  flex-shrink: 0;
  background: transparent;
  border: none;
  color: var(--muted);
  cursor: pointer;
  padding: 0;
}

.chevron {
  transition: transform 0.15s ease;
}

.chevron--open {
  transform: rotate(90deg);
}

.workspace-name {
  flex: 1;
  min-width: 0;
  font-size: 12px;
  font-weight: 500;
  color: var(--text);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.rename-input {
  flex: 1;
  min-width: 0;
}

.session-count {
  font-size: 9px;
  font-weight: 600;
  padding: 1px 4px;
  border-radius: 3px;
  background: rgba(255, 255, 255, 0.08);
  color: var(--muted);
  flex-shrink: 0;
}

.overflow-btn {
  display: flex;
  align-items: center;
  justify-content: center;
  width: 20px;
  height: 20px;
  background: transparent;
  border: none;
  border-radius: 3px;
  color: var(--muted);
  cursor: pointer;
  opacity: 0;
  transition: opacity 0.1s ease, color 0.1s ease, background-color 0.1s ease;
  flex-shrink: 0;
  padding: 0;
}

.overflow-btn:hover {
  background: rgba(255, 255, 255, 0.08);
  color: var(--text);
  opacity: 1;
}

.collapsible-content {
  overflow: hidden;
}

.sessions-list {
  padding-left: 16px;
  padding-top: 2px;
  padding-bottom: 4px;
  display: flex;
  flex-direction: column;
  gap: 1px;
}

.children-list {
  padding-left: 12px;
  border-left: 2px solid rgba(161, 161, 170, 0.2);
  margin-left: 8px;
  margin-top: 1px;
  display: flex;
  flex-direction: column;
  gap: 1px;
}

.empty-state {
  padding: 8px 16px;
  font-size: 10px;
  color: var(--muted);
  font-style: italic;
  opacity: 0.7;
}

.menu-item {
  font-size: 11px;
  gap: 6px;
}

.menu-item--destructive {
  color: #ef4444;
}
</style>
