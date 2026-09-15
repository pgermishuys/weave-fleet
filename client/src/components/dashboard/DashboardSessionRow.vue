<script setup lang="ts">
import { computed, shallowRef } from "vue";
import { Archive, Trash2 } from "lucide-vue-next";
import type { SessionListItem } from "@/api/client";
import ConfirmDeleteSessionDialog from "@/components/sessions/ConfirmDeleteSessionDialog.vue";
import ProgressRing from "@/components/sessions/ProgressRing.vue";
import StatusGlyph from "@/components/sessions/StatusGlyph.vue";
import { useArchiveSession, useDeleteSession } from "@/composables/use-session-actions";
import { sessionCache } from "@/lib/session-cache";
import { isSessionLive, sessionRowStatus } from "@/lib/session-row-status";
import { dispatchSessionRemoved } from "@/lib/session-sync";
import { useSessionsStore } from "@/stores/sessions";

/** One session on the dashboard: what it is, where, how far along, and what it needs. */
const props = defineProps<{
  session: SessionListItem;
  now: number;
}>();

const emit = defineEmits<{
  select: [session: SessionListItem];
  changed: [];
}>();

const sessionsStore = useSessionsStore();
const { archiveSession, isArchiving } = useArchiveSession();
const { deleteSession, isDeleting } = useDeleteSession();
const isDeleteDialogOpen = shallowRef(false);

const sessionId = computed(() => props.session.session.id);
const title = computed(() => props.session.session.title?.trim() || "Untitled session");
const isLive = computed(() => isSessionLive(props.session));
const status = computed(() => sessionRowStatus(props.session, props.now));
const isArchived = computed(() => props.session.retentionStatus === "archived");
const canArchive = computed(() =>
  props.session.capabilities?.canArchive ?? (!isArchived.value && props.session.lifecycleStatus !== "running"));
const canDelete = computed(() => props.session.capabilities?.canDelete ?? true);
const isPending = computed(() => isArchiving.value || isDeleting.value);

const place = computed(() => {
  const workspace = props.session.projectName?.trim()
    || props.session.workspaceDisplayName?.trim()
    || lastSegment(props.session.workspaceDirectory);
  return [workspace, props.session.branch].filter(Boolean).join(" · ");
});

const progress = computed(() => {
  const summary = props.session.progress;
  return summary && summary.total > 0 ? summary : null;
});

function lastSegment(path: string): string {
  const parts = path.split(/[\\/]/).filter(Boolean);
  return parts.at(-1) ?? path;
}

async function handleArchive(): Promise<void> {
  if (!canArchive.value) return;
  try {
    await archiveSession(sessionId.value);
    sessionsStore.patchSession(sessionId.value, { retentionStatus: "archived" });
    emit("changed");
  } catch {
    // Mutation state is owned by the composable.
  }
}

async function handleDelete(): Promise<void> {
  if (!canDelete.value) return;
  try {
    await deleteSession(sessionId.value, props.session.instanceId);
    isDeleteDialogOpen.value = false;
    sessionCache.delete(sessionId.value, props.session.instanceId);
    dispatchSessionRemoved(sessionId.value);
    sessionsStore.removeSession(sessionId.value);
    emit("changed");
  } catch {
    // Mutation state is owned by the composable.
  }
}
</script>

<template>
  <div
    class="dash-row"
    :data-session-id="sessionId"
    data-testid="dashboard-session-row"
    role="button"
    tabindex="0"
    :aria-label="`${title}, ${status.description}`"
    @click="emit('select', session)"
    @keydown.enter.prevent="emit('select', session)"
    @keydown.space.prevent="emit('select', session)"
  >
    <span class="dash-row__glyph">
      <StatusGlyph
        v-if="isLive"
        :status="session.sessionStatus"
        :activity="session.activityStatus"
        :label="status.description"
      />
      <span
        v-else
        class="dash-row__quiet-dot"
        aria-hidden="true"
      />
    </span>

    <span class="dash-row__main">
      <span class="dash-row__title">{{ title }}</span>
      <span
        v-if="place"
        class="dash-row__place"
      >{{ place }}</span>
    </span>

    <span
      v-if="progress"
      class="dash-row__progress"
      :title="progress.current ? `Now: ${progress.current}` : undefined"
    >
      <span
        v-if="progress.current && isLive"
        class="dash-row__current"
      >{{ progress.current }}</span>
      <ProgressRing
        :done="progress.done"
        :total="progress.total"
      />
      <span class="dash-row__count">{{ progress.done }}/{{ progress.total }}</span>
    </span>

    <span
      v-if="isArchived"
      class="dash-row__tag"
    >Archived</span>

    <span
      v-if="status.label"
      class="dash-row__status"
      :class="`dash-row__status--${status.tone}`"
    >{{ status.label }}</span>

    <span
      v-if="canArchive || canDelete"
      class="dash-row__actions"
    >
      <button
        v-if="canArchive"
        type="button"
        class="dash-row__action"
        data-testid="session-archive-button"
        :disabled="isPending"
        aria-label="Archive session"
        title="Archive"
        @click.stop="void handleArchive()"
        @keydown.stop
      >
        <Archive
          :size="14"
          aria-hidden="true"
        />
      </button>
      <button
        v-if="canDelete"
        type="button"
        class="dash-row__action dash-row__action--danger"
        data-testid="session-delete-button"
        :disabled="isPending"
        aria-label="Delete session"
        title="Delete"
        @click.stop="isDeleteDialogOpen = true"
        @keydown.stop
      >
        <Trash2
          :size="14"
          aria-hidden="true"
        />
      </button>
    </span>
  </div>

  <ConfirmDeleteSessionDialog
    v-model:open="isDeleteDialogOpen"
    :is-deleting="isDeleting"
    :session-title="title"
    @confirm="void handleDelete()"
  />
</template>

<style scoped>
.dash-row {
  --dash-row-hover: color-mix(in srgb, var(--text) 5%, var(--panel-bg));
  position: relative;
  display: flex;
  align-items: center;
  gap: 10px;
  min-height: 48px;
  padding: 6px 10px;
  border-radius: var(--radius-btn);
  color: var(--text);
  cursor: pointer;
  transition: background-color var(--transition);
}

.dash-row:hover,
.dash-row:focus-within {
  background: var(--dash-row-hover);
}

.dash-row:focus-visible {
  outline: 2px solid var(--accent);
  outline-offset: -2px;
}

.dash-row__glyph {
  display: grid;
  flex-shrink: 0;
  place-items: center;
  width: 14px;
  height: 14px;
}

.dash-row__quiet-dot {
  width: 6px;
  height: 6px;
  border-radius: 50%;
  background: color-mix(in srgb, var(--muted) 45%, transparent);
}

.dash-row__main {
  display: flex;
  flex: 1;
  flex-direction: column;
  min-width: 0;
}

.dash-row__title {
  overflow: hidden;
  font-size: 13px;
  font-weight: 500;
  line-height: 1.35;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.dash-row__place {
  overflow: hidden;
  color: var(--muted);
  font-size: 12px;
  line-height: 1.35;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.dash-row__progress {
  display: inline-flex;
  flex-shrink: 1;
  align-items: center;
  gap: 6px;
  min-width: 0;
  max-width: 45%;
}

.dash-row__current {
  overflow: hidden;
  color: var(--muted);
  font-size: 12px;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.dash-row__count {
  flex-shrink: 0;
  color: var(--muted);
  font-size: 12px;
  font-variant-numeric: tabular-nums;
}

.dash-row__tag {
  flex-shrink: 0;
  padding: 1px 7px;
  border-radius: var(--radius-btn);
  background: color-mix(in srgb, var(--text) 7%, transparent);
  color: var(--muted);
  font-size: 11.5px;
}

.dash-row__status {
  flex-shrink: 0;
  min-width: 28px;
  color: color-mix(in srgb, var(--muted) 85%, transparent);
  font-size: 12px;
  font-variant-numeric: tabular-nums;
  text-align: right;
  white-space: nowrap;
}

.dash-row__status--attention {
  padding: 1px 7px;
  border-radius: var(--radius-btn);
  background: color-mix(in srgb, var(--status-waiting) 14%, transparent);
  color: var(--status-waiting);
  font-weight: 500;
}

.dash-row__status--error {
  color: var(--error);
  font-weight: 500;
}

.dash-row__status--retry {
  color: var(--status-waiting);
}

/* Actions float over the right edge on hover, so the row doesn't reflow. */
.dash-row__actions {
  position: absolute;
  top: 50%;
  right: 6px;
  display: flex;
  gap: 2px;
  padding-left: 16px;
  background: linear-gradient(to right, transparent, var(--dash-row-hover) 14px);
  opacity: 0;
  transform: translateY(-50%);
  transition: opacity 120ms ease-out;
  pointer-events: none;
}

.dash-row:hover .dash-row__actions,
.dash-row:focus-within .dash-row__actions {
  opacity: 1;
  pointer-events: auto;
}

.dash-row__action {
  display: grid;
  place-items: center;
  width: 28px;
  height: 28px;
  border: 0;
  border-radius: var(--radius-btn);
  background: transparent;
  color: var(--muted);
  cursor: pointer;
  transition: background-color var(--transition), color var(--transition);
}

.dash-row__action:hover {
  background: color-mix(in srgb, var(--text) 8%, transparent);
  color: var(--text);
}

.dash-row__action--danger:hover {
  background: color-mix(in srgb, var(--error) 12%, transparent);
  color: var(--error);
}

.dash-row__action:disabled {
  opacity: 0.5;
  cursor: default;
}

@media (max-width: 640px) {
  .dash-row__current {
    display: none;
  }
}

@media (hover: none) {
  .dash-row__actions {
    display: none;
  }
}

@media (prefers-reduced-motion: reduce) {
  .dash-row,
  .dash-row__actions,
  .dash-row__action {
    transition: none;
  }
}
</style>
