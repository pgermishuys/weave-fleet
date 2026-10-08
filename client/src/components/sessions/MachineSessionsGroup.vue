<script setup lang="ts">
import { computed, watch } from "vue";
import type { SessionListItem } from "@/api/client";
import MachineHeader from "@/components/sessions/MachineHeader.vue";
import ProjectGroup from "@/components/sessions/ProjectGroup.vue";
import { splitPinned } from "@/lib/session-pins";
import {
  PINNED_GROUP_ID,
  buildProjectGroups,
  draftGroupIdFor,
  draftGroupKeyFor,
  filterProjectGroups,
  pinnedProjectGroup,
  sessionMatchesQuery,
} from "@/lib/session-project-groups";
import { useMachinesStore, type MachineEntry, type MachineSessions } from "@/stores/machines";
import type { NewSessionDraftRow } from "@/stores/workspace-ui";
import { machineGroupKey, projectGroupKey, useSidebarStore } from "@/stores/sidebar";

/**
 * A machine the app isn't working in: its sessions as it last listed them, refreshed on a timer, in the same tree the
 * live machine shows (Pinned, its projects in their order, what each session started under it). Its rows only open
 * the session, which makes this machine live; archiving, moving and renaming happen once it's live, so nothing here
 * can act on the wrong machine.
 */
const props = defineProps<{
  machine: MachineEntry;
  state: MachineSessions | undefined;
  /** Lower-case filter from the sidebar's search box. */
  query: string;
  /** The new-session draft, when it starts on this machine. */
  draft?: NewSessionDraftRow | null;
  draftActive?: boolean;
}>();

const emit = defineEmits<{ open: [session: SessionListItem]; openDraft: []; newSession: []; workHere: [] }>();

const sidebar = useSidebarStore();
const expanded = computed(() => !sidebar.isGroupCollapsed(machineGroupKey(props.machine.key)));

const projects = computed(() => props.state?.projects ?? []);
const projectsById = computed(() => new Map(projects.value.map((project) => [project.id, project])));

const sessions = computed(() => (props.state?.sessions ?? [])
  .filter((item) => !item.parentSessionId && item.retentionStatus !== "archived"));

const split = computed(() => splitPinned(sessions.value));

function matches(item: SessionListItem): boolean {
  return sessionMatchesQuery(item, props.query, projectsById.value);
}

const draftKey = computed(() => (props.draft ? draftGroupKeyFor(props.draft.projectId, projects.value) : null));
const draftGroupId = computed(() => draftGroupIdFor(draftKey.value, projects.value));

const pinnedGroup = computed(() => pinnedProjectGroup(props.query ? split.value.pinned.filter(matches) : split.value.pinned));

const projectGroups = computed(() => {
  const groups = buildProjectGroups(split.value.rest, projects.value, draftKey.value);
  return props.query ? filterProjectGroups(groups, props.query, matches) : groups;
});

const count = computed(() => pinnedGroup.value.sessionCount
  + projectGroups.value.reduce((total, group) => total + group.sessionCount, 0));

/** What each session's agent had running when the list came back: the chip on its row. */
const runningCounts = computed(() => new Map(sessions.value
  .filter((item) => item.runningWorkCount)
  .map((item) => [item.session.id, item.runningWorkCount!])));

function isProjectExpanded(groupId: string): boolean {
  return !sidebar.isGroupCollapsed(projectGroupKey(props.machine.key, groupId));
}

function toggleProject(groupId: string): void {
  sidebar.toggleGroupCollapsed(projectGroupKey(props.machine.key, groupId));
}

// A draft shows in its group, so that group opens when a draft lands in it.
watch(draftGroupId, (groupId) => {
  if (groupId) sidebar.setGroupCollapsed(projectGroupKey(props.machine.key, groupId), false);
});

const machines = useMachinesStore();
const unreachable = computed(() => Boolean(props.state?.error));

const note = computed(() => {
  if (props.state?.error) return props.state.loadedAt ? "unreachable · cached" : "unreachable";
  if (!props.state?.loadedAt) return props.state?.loading ? "…" : null;
  return null;
});
</script>

<template>
  <section
    class="machine-group"
    :class="{ 'machine-group--stale': unreachable }"
    :aria-label="`Sessions on ${machine.name}`"
    data-testid="machine-group"
    :data-machine="machine.key"
  >
    <MachineHeader
      :name="machine.name"
      :unreachable="unreachable"
      :note="note"
      :count="count"
      :expanded="expanded"
      menu
      @toggle="sidebar.toggleGroupCollapsed(machineGroupKey(machine.key))"
      @new-session="emit('newSession')"
      @work-here="emit('workHere')"
    />

    <template v-if="expanded">
      <p
        v-if="state?.error"
        class="machine-group__error"
        role="status"
      >
        {{ state.error }}
      </p>
      <ProjectGroup
        v-if="pinnedGroup.sessionCount > 0"
        key="pinned"
        :project="pinnedGroup"
        pinned
        :expanded="isProjectExpanded(PINNED_GROUP_ID)"
        :active-session-id="null"
        :active-drag-session-id="null"
        :active-drag-project-id="null"
        :running-counts="runningCounts"
        :open-on-machine="machine.name"
        :machine-not-answering="!machines.isAnswering(machine.key)"
        data-testid="pinned-group"
        @toggle="toggleProject"
        @select-session="emit('open', $event)"
      />
      <ProjectGroup
        v-for="project in projectGroups"
        :key="project.id"
        :project="project"
        :expanded="isProjectExpanded(project.id)"
        :active-session-id="null"
        :active-drag-session-id="null"
        :active-drag-project-id="null"
        :draft="project.id === draftGroupId ? draft : null"
        :draft-active="draftActive"
        :running-counts="runningCounts"
        :open-on-machine="machine.name"
        :machine-not-answering="!machines.isAnswering(machine.key)"
        @toggle="toggleProject"
        @select-session="emit('open', $event)"
        @open-draft="emit('openDraft')"
      />
      <p
        v-if="!state?.error && state?.loadedAt && projectGroups.length === 0 && pinnedGroup.sessionCount === 0"
        class="machine-group__empty"
      >
        <template v-if="query">
          No matching sessions
        </template>
        <template v-else>
          No sessions ·
          <button
            type="button"
            class="machine-group__start"
            data-testid="machine-group-new-session"
            @click="emit('newSession')"
          >
            New session
          </button>
        </template>
      </p>
    </template>
  </section>
</template>

<style scoped>
.machine-group__error,
.machine-group__empty {
  margin: 2px 10px 4px 26px;
  font-size: 11.5px;
  line-height: 1.4;
  color: var(--muted);
}

.machine-group__start {
  padding: 0;
  border: 0;
  background: none;
  color: var(--coral);
  font: inherit;
  cursor: pointer;
}

.machine-group__start:hover {
  text-decoration: underline;
}

.machine-group__start:focus-visible {
  outline: 2px solid var(--accent);
  outline-offset: 2px;
  border-radius: 2px;
}

.machine-group__error {
  color: color-mix(in srgb, var(--error) 80%, var(--muted));
}

/* An unreachable machine's rows are the last ones it returned. */
.machine-group--stale :deep(.session-item) {
  opacity: 0.6;
}
</style>
