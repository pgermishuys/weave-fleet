<script setup lang="ts">
import { computed } from "vue";
import { useRouter } from "@tanstack/vue-router";
import { Bot } from "lucide-vue-next";
import { subagentTitle, type SessionSubagent } from "@/lib/session-progress";
import { useSessionsStore } from "@/stores/sessions";

/** A subagent in the Progress tab: what it's doing, its own count, and a link to its session. */
const props = defineProps<{
  subagent: SessionSubagent;
  parentSessionId: string;
}>();

const router = useRouter();
const sessionsStore = useSessionsStore();

const finished = computed(() => ["completed", "error", "cancelled"].includes(props.subagent.status));
const task = computed(() => subagentTitle(props.subagent));

const status = computed(() => {
  switch (props.subagent.status) {
    case "completed": return "Finished";
    case "error": return "Failed";
    case "cancelled": return "Cancelled";
    case "running": return props.subagent.current ? `Now: ${props.subagent.current}` : "Working";
    default: return "Starting";
  }
});

const childInstanceId = computed(() => {
  const childId = props.subagent.childSessionId;
  if (!childId) return null;
  return sessionsStore.sessions.find((session) => session.session.id === childId)?.instanceId ?? childId;
});

const href = computed(() => {
  const childId = props.subagent.childSessionId;
  if (!childId || !childInstanceId.value) return undefined;
  const search = new URLSearchParams({ instanceId: childInstanceId.value, parentSessionId: props.parentSessionId });
  return `/sessions/${encodeURIComponent(childId)}?${search.toString()}`;
});

function open(event: MouseEvent): void {
  const childId = props.subagent.childSessionId;
  if (!childId || !childInstanceId.value) return;
  event.preventDefault();
  void router.navigate({
    to: "/sessions/$id",
    params: { id: childId },
    search: { instanceId: childInstanceId.value, parentSessionId: props.parentSessionId },
  });
}
</script>

<template>
  <component
    :is="href ? 'a' : 'div'"
    class="progress-subagent"
    :class="{ 'progress-subagent--finished': finished }"
    :href="href"
    @click="open"
  >
    <Bot
      :size="14"
      class="progress-subagent__icon"
      aria-hidden="true"
    />
    <span class="progress-subagent__title"><strong>{{ subagent.agent }}</strong><template v-if="task"> · {{ task }}</template></span>
    <span
      v-if="subagent.total > 0"
      class="progress-subagent__count"
    >{{ subagent.done }}/{{ subagent.total }}</span>
    <span
      v-if="subagent.total > 0"
      class="progress-subagent__bar"
      aria-hidden="true"
    ><span :style="{ width: `${(subagent.done / subagent.total) * 100}%` }" /></span>
    <span class="progress-subagent__status">{{ status }}</span>
  </component>
</template>

<style scoped>
.progress-subagent {
  display: grid;
  grid-template-columns: auto minmax(0, 1fr) auto;
  align-items: center;
  gap: 4px 8px;
  padding: 7px 9px;
  border: 1px solid var(--border);
  border-radius: var(--radius-btn);
  background: var(--card-bg, var(--panel-bg));
  color: var(--text);
  text-decoration: none;
  transition: border-color var(--transition);
}

a.progress-subagent:hover {
  border-color: color-mix(in srgb, var(--text) 18%, transparent);
}

a.progress-subagent:focus-visible {
  outline: 2px solid var(--accent);
  outline-offset: 1px;
}

.progress-subagent__icon {
  color: var(--muted);
}

.progress-subagent__title {
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  font-size: 12px;
}

.progress-subagent__title strong {
  font-weight: 600;
}

.progress-subagent__count {
  font-size: 11.5px;
  color: var(--muted);
  font-variant-numeric: tabular-nums;
}

.progress-subagent__bar {
  grid-column: 1 / 4;
  height: 3px;
  overflow: hidden;
  border-radius: 2px;
  background: color-mix(in srgb, var(--text) 9%, transparent);
}

.progress-subagent__bar span {
  display: block;
  height: 100%;
  background: var(--accent);
  transition: width var(--transition);
}

.progress-subagent--finished .progress-subagent__bar span {
  background: var(--running);
}

.progress-subagent__status {
  grid-column: 1 / 4;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  font-size: 11.5px;
  color: var(--muted);
}
</style>
