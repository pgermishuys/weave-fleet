<script setup lang="ts">
import { computed, shallowRef } from "vue";
import { ArrowDown, ArrowUp, ChevronDown, MoreHorizontal, Pencil, Plus, Trash2 } from "lucide-vue-next";
import {
  ContextMenu,
  ContextMenuContent,
  ContextMenuItem,
  ContextMenuSeparator,
  ContextMenuShortcut,
  ContextMenuTrigger,
} from "@/components/ui/context-menu";
import {
  useDeleteProject,
  useReorderProject,
  useUpdateProject,
  type DeleteProjectMode,
} from "@/composables/use-session-actions";
import type { SessionListItem } from "@/lib/api-types";
import ConfirmDeleteProjectDialog from "./ConfirmDeleteProjectDialog.vue";
import InlineEdit from "./InlineEdit.vue";
import SessionItem from "./SessionItem.vue";

interface ProjectSessionGroup {
  id: string;
  label: string;
  sessions: SessionListItem[];
}

interface ProjectGroupModel {
  id: string;
  projectId: string | null;
  name: string;
  color: string;
  isUngrouped: boolean;
  canMoveUp: boolean;
  canMoveDown: boolean;
  moveUpTargets: Array<{ projectId: string; position: number }>;
  moveDownTargets: Array<{ projectId: string; position: number }>;
  sessionCount: number;
  subgroups: ProjectSessionGroup[];
}

interface Props {
  project: ProjectGroupModel;
  expanded: boolean;
  activeSessionId: string | null;
}

interface Emits {
  toggle: [projectId: string];
  selectSession: [session: SessionListItem];
  newSession: [projectId: string];
  projectChanged: [];
  sessionChanged: [];
}

const props = defineProps<Props>();
const emit = defineEmits<Emits>();

const isContextMenuOpen = shallowRef(false);
const isInlineEditing = shallowRef(false);
const isDeleteDialogOpen = shallowRef(false);

const {
  updateProject,
  isUpdating,
} = useUpdateProject();
const {
  reorderProject,
  isReordering,
} = useReorderProject();
const {
  deleteProject,
  isDeleting,
} = useDeleteProject();

const canShowContextMenu = computed(() => !props.project.isUngrouped && props.project.projectId !== null);
const sessionCountLabel = computed(() => `${props.project.sessionCount} session${props.project.sessionCount === 1 ? "" : "s"}`);
const isAnyActionPending = computed(() => isUpdating.value || isReordering.value || isDeleting.value);

function handleToggle(): void {
  emit("toggle", props.project.id);
}

function handleSessionSelect(session: SessionListItem): void {
  emit("selectSession", session);
}

function handleContextMenuOpenChange(value: boolean): void {
  isContextMenuOpen.value = value;
}

function startRename(): void {
  if (!canShowContextMenu.value || isAnyActionPending.value) {
    return;
  }

  isContextMenuOpen.value = false;
  isInlineEditing.value = true;
}

function cancelRename(): void {
  isInlineEditing.value = false;
}

function handleHeaderKeydown(event: KeyboardEvent): void {
  if (event.key !== "F2") {
    return;
  }

  event.preventDefault();
  startRename();
}

function handleNewSessionRequest(): void {
  if (!props.project.projectId) {
    return;
  }

  isContextMenuOpen.value = false;
  emit("newSession", props.project.projectId);
}

async function handleRename(nextName: string): Promise<void> {
  const trimmedName = nextName.trim();
  isInlineEditing.value = false;

  if (!props.project.projectId || trimmedName.length === 0 || trimmedName === props.project.name) {
    return;
  }

  try {
    await updateProject(props.project.projectId, { name: trimmedName });
    emit("projectChanged");
  } catch {
    // Errors are handled by the mutation composable state.
  }
}

async function handleMoveUp(): Promise<void> {
  if (!props.project.projectId || props.project.moveUpTargets.length === 0) {
    return;
  }

  isContextMenuOpen.value = false;

  try {
    for (const target of props.project.moveUpTargets) {
      await reorderProject(target.projectId, target.position);
    }

    emit("projectChanged");
  } catch {
    // Errors are handled by the mutation composable state.
  }
}

async function handleMoveDown(): Promise<void> {
  if (!props.project.projectId || props.project.moveDownTargets.length === 0) {
    return;
  }

  isContextMenuOpen.value = false;

  try {
    for (const target of props.project.moveDownTargets) {
      await reorderProject(target.projectId, target.position);
    }

    emit("projectChanged");
  } catch {
    // Errors are handled by the mutation composable state.
  }
}

function openDeleteDialog(): void {
  if (!canShowContextMenu.value) {
    return;
  }

  isContextMenuOpen.value = false;
  isDeleteDialogOpen.value = true;
}

async function handleDelete(mode: DeleteProjectMode): Promise<void> {
  if (!props.project.projectId) {
    return;
  }

  try {
    await deleteProject(props.project.projectId, mode);
    isDeleteDialogOpen.value = false;
    emit("projectChanged");
  } catch {
    // Errors are handled by the mutation composable state.
  }
}
</script>

<template>
  <section
    class="project-group"
    :data-project-id="project.projectId"
  >
    <ContextMenu
      v-if="canShowContextMenu && !isInlineEditing"
      :open="isContextMenuOpen"
      @update:open="handleContextMenuOpenChange"
    >
      <ContextMenuTrigger as-child>
        <div class="project-shell">
          <button
            type="button"
            class="project-header"
            :class="{ collapsed: !expanded }"
            :aria-expanded="expanded"
            @click="handleToggle"
            @keydown="handleHeaderKeydown"
          >
            <ChevronDown
              class="project-chevron"
              aria-hidden="true"
            />
            <span
              class="project-dot"
              :style="{ backgroundColor: project.color }"
              aria-hidden="true"
            />

            <span class="project-copy">
              <span class="project-title">{{ project.name }}</span>
              <span class="project-count">{{ sessionCountLabel }}</span>
            </span>

            <span class="project-spacer" />
            <span
              class="project-actions"
              aria-hidden="true"
            >
              <MoreHorizontal class="project-actions__icon" />
            </span>
          </button>
        </div>
      </ContextMenuTrigger>

      <ContextMenuContent class="w-56">
        <ContextMenuItem
          :disabled="isAnyActionPending"
          @select="handleNewSessionRequest"
        >
          <Plus class="h-4 w-4" />
          New Session
        </ContextMenuItem>

        <ContextMenuItem
          :disabled="isAnyActionPending"
          @select="startRename"
        >
          <Pencil class="h-4 w-4" />
          Rename
          <ContextMenuShortcut>F2</ContextMenuShortcut>
        </ContextMenuItem>

        <ContextMenuItem
          v-if="project.canMoveUp"
          :disabled="isAnyActionPending"
          @select="handleMoveUp"
        >
          <ArrowUp class="h-4 w-4" />
          Move Up
        </ContextMenuItem>

        <ContextMenuItem
          v-if="project.canMoveDown"
          :disabled="isAnyActionPending"
          @select="handleMoveDown"
        >
          <ArrowDown class="h-4 w-4" />
          Move Down
        </ContextMenuItem>

        <ContextMenuSeparator />

        <ContextMenuItem
          variant="destructive"
          :disabled="isAnyActionPending"
          @select="openDeleteDialog"
        >
          <Trash2 class="h-4 w-4" />
          Delete
        </ContextMenuItem>
      </ContextMenuContent>
    </ContextMenu>

    <div
      v-else
      class="project-shell"
    >
      <button
        v-if="!isInlineEditing"
        type="button"
        class="project-header"
        :class="{ collapsed: !expanded }"
        :aria-expanded="expanded"
        @click="handleToggle"
        @keydown="handleHeaderKeydown"
      >
        <ChevronDown
          class="project-chevron"
          aria-hidden="true"
        />
        <span
          class="project-dot"
          :style="{ backgroundColor: project.color }"
          aria-hidden="true"
        />

        <span class="project-copy">
          <span class="project-title">{{ project.name }}</span>
          <span class="project-count">{{ sessionCountLabel }}</span>
        </span>

        <span class="project-spacer" />
      </button>

      <div
        v-else
        class="project-header project-header--editing"
        :class="{ collapsed: !expanded }"
      >
        <ChevronDown
          class="project-chevron"
          aria-hidden="true"
        />
        <span
          class="project-dot"
          :style="{ backgroundColor: project.color }"
          aria-hidden="true"
        />

        <span class="project-copy">
          <InlineEdit
            :initial-value="project.name"
            :disabled="isUpdating"
            placeholder="Project name"
            @cancel="cancelRename"
            @commit="handleRename"
          />
          <span class="project-count">{{ sessionCountLabel }}</span>
        </span>

        <span class="project-spacer" />
      </div>
    </div>

    <Transition name="collapse">
      <div
        v-if="expanded"
        class="project-content"
      >
        <div
          v-for="subgroup in project.subgroups"
          :key="subgroup.id"
          class="project-subgroup"
        >
          <p class="project-subgroup__label">
            {{ subgroup.label }}
          </p>

          <SessionItem
            v-for="session in subgroup.sessions"
            :key="session.session.id"
            :session="session"
            :active="session.session.id === activeSessionId"
            @changed="emit('sessionChanged')"
            @select="handleSessionSelect"
          />
        </div>
      </div>
    </Transition>

    <ConfirmDeleteProjectDialog
      v-model:open="isDeleteDialogOpen"
      :project-name="project.name"
      :session-count="project.sessionCount"
      :is-deleting="isDeleting"
      @confirm="handleDelete"
    />
  </section>
</template>

<style scoped>
.project-group {
  margin-bottom: 2px;
}

.project-shell {
  width: 100%;
}

.project-header {
  width: 100%;
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 8px 12px;
  cursor: pointer;
  border: 0;
  background: transparent;
  color: var(--text);
  text-align: left;
  transition: background-color 0.25s ease;
}

.project-header:hover {
  background: rgba(255, 255, 255, 0.03);
}

.project-header--editing {
  cursor: default;
}

.project-header--editing:hover {
  background: transparent;
}

.project-header:focus-visible {
  outline: 2px solid var(--accent);
  outline-offset: -2px;
}

.project-chevron {
  font-size: 10px;
  color: var(--muted);
  width: 14px;
  text-align: center;
  transition: transform 0.25s ease;
}

.project-header.collapsed .project-chevron {
  transform: rotate(-90deg);
}

.project-dot {
  width: 8px;
  height: 8px;
  border-radius: 50%;
  flex-shrink: 0;
}

.project-copy {
  min-width: 0;
  display: flex;
  flex-direction: column;
  gap: 2px;
}

.project-title {
  font-size: 13px;
  font-weight: 600;
  line-height: 1.2;
}

.project-count {
  font-size: 11px;
  color: var(--muted);
}

.project-spacer {
  flex: 1;
}

.project-actions {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 22px;
  height: 22px;
  border-radius: 6px;
  color: var(--muted);
  pointer-events: none;
}

.project-actions__icon {
  width: 14px;
  height: 14px;
}

.project-content {
  padding-bottom: 2px;
  overflow: hidden;
}

.project-subgroup {
  padding-bottom: 4px;
}

.project-subgroup__label {
  margin: 4px 12px 4px 38px;
  font-size: 10px;
  font-weight: 600;
  letter-spacing: 0.05em;
  text-transform: uppercase;
  color: var(--muted);
}
</style>
