<script setup lang="ts">
import { computed } from "vue";
import type { SessionListItem } from "@/api/client";
import MachineHeader from "@/components/sessions/MachineHeader.vue";
import StatusGlyph from "@/components/sessions/StatusGlyph.vue";
import { useRelativeTime } from "@/composables/use-relative-time";
import { formatCompactAge, isSessionLive, sessionRowDim, sessionRowStatus } from "@/lib/session-row-status";
import type { MachineEntry, MachineSessions } from "@/stores/machines";
import { machineGroupKey, useSidebarStore } from "@/stores/sidebar";

/**
 * A machine the app isn't working in: its sessions as it last listed them, refreshed on a timer. Its rows only
 * open the session, which makes this machine live; archiving, moving and renaming happen once it's live,
 * so nothing here can act on the wrong machine.
 */
const props = defineProps<{
  machine: MachineEntry;
  state: MachineSessions | undefined;
  /** Lower-case filter from the sidebar's search box. */
  query: string;
}>();

const emit = defineEmits<{ open: [session: SessionListItem] }>();

const sidebar = useSidebarStore();
const expanded = computed(() => !sidebar.isGroupCollapsed(machineGroupKey(props.machine.key)));
const now = useRelativeTime();

const sessions = computed(() => {
  const all = (props.state?.sessions ?? []).filter((item) => !item.parentSessionId && item.retentionStatus !== "archived");
  return props.query
    ? all.filter((item) => (item.session.title ?? "").toLowerCase().includes(props.query))
    : all;
});

const unreachable = computed(() => Boolean(props.state?.error));

const note = computed(() => {
  if (props.state?.error) return props.state.loadedAt ? "unreachable · cached" : "unreachable";
  if (!props.state?.loadedAt) return props.state?.loading ? "…" : null;
  return null;
});

function title(item: SessionListItem): string {
  return item.session.title?.trim() || "Untitled session";
}

function age(item: SessionListItem): string {
  const time = item.session.time;
  return formatCompactAge(time?.updated ?? time?.created ?? "", now.value);
}
</script>

<template>
  <section
    class="machine-group"
    :aria-label="`Sessions on ${machine.name}`"
    data-testid="machine-group"
    :data-machine="machine.key"
  >
    <MachineHeader
      :name="machine.name"
      :unreachable="unreachable"
      :note="note"
      :count="sessions.length"
      :expanded="expanded"
      @toggle="sidebar.toggleGroupCollapsed(machineGroupKey(machine.key))"
    />

    <template v-if="expanded">
      <p
        v-if="state?.error"
        class="machine-group__error"
        role="status"
      >
        {{ state.error }}
      </p>
      <button
        v-for="item in sessions"
        :key="item.session.id"
        type="button"
        class="machine-row"
        :class="[
          { 'machine-row--stale': unreachable },
          sessionRowDim(item, now) > 0 ? `machine-row--dim-${sessionRowDim(item, now)}` : '',
        ]"
        :title="`Open on ${machine.name}`"
        data-testid="machine-session-row"
        :data-session-id="item.session.id"
        @click="emit('open', item)"
      >
        <StatusGlyph
          v-if="isSessionLive(item)"
          :status="item.sessionStatus"
          :activity="item.activityStatus"
          :label="sessionRowStatus(item, now).description"
        />
        <span
          v-else
          class="machine-row__slot"
          aria-hidden="true"
        />
        <span class="machine-row__title">{{ title(item) }}</span>
        <span
          class="machine-row__meta"
          :class="`machine-row__meta--${sessionRowStatus(item, now).tone}`"
        >{{ sessionRowStatus(item, now).label || age(item) }}</span>
      </button>
      <p
        v-if="!state?.error && state?.loadedAt && sessions.length === 0"
        class="machine-group__empty"
      >
        {{ query ? "No matching sessions" : "No sessions" }}
      </p>
    </template>
  </section>
</template>

<style scoped>
.machine-group {
  display: flex;
  flex-direction: column;
  gap: 1px;
}

.machine-group__error,
.machine-group__empty {
  margin: 2px 10px 4px 26px;
  font-size: 11.5px;
  line-height: 1.4;
  color: var(--muted);
}

.machine-group__error {
  color: color-mix(in srgb, var(--error) 80%, var(--muted));
}

.machine-row {
  width: 100%;
  min-width: 0;
  min-height: 30px;
  display: flex;
  align-items: center;
  gap: 9px;
  padding: 0 10px;
  border: 0;
  border-radius: var(--radius-btn);
  background: transparent;
  color: color-mix(in srgb, var(--text) 80%, transparent);
  text-align: left;
  font: inherit;
  cursor: pointer;
  transition: background var(--transition), color var(--transition);
}

.machine-row:hover {
  background: color-mix(in srgb, var(--text) 5%, transparent);
  color: var(--text);
}

.machine-row:focus-visible {
  outline: 2px solid var(--accent);
  outline-offset: -2px;
}

.machine-row--dim-1 {
  color: color-mix(in srgb, var(--text) 55%, transparent);
}

.machine-row--dim-2,
.machine-row--stale {
  color: color-mix(in srgb, var(--text) 40%, transparent);
}

.machine-row__slot {
  width: 8px;
  height: 8px;
  flex-shrink: 0;
}

.machine-row__title {
  flex: 1 1 auto;
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  font-size: 13px;
  line-height: 1.3;
}

.machine-row__meta {
  flex-shrink: 0;
  font-size: 11px;
  color: color-mix(in srgb, var(--muted) 80%, transparent);
  font-variant-numeric: tabular-nums;
}

.machine-row__meta--attention {
  color: var(--status-waiting);
}

.machine-row__meta--error {
  color: var(--error);
}
</style>
