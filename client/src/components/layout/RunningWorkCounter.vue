<script setup lang="ts">
import { computed, shallowRef } from "vue";
import { storeToRefs } from "pinia";
import { useRouter } from "@tanstack/vue-router";
import BackgroundWorkRow from "@/components/session/BackgroundWorkRow.vue";
import { Popover, PopoverContent, PopoverTrigger } from "@/components/ui/popover";
import { useRunningWorkAcrossSessions } from "@/composables/use-running-work";
import { useSessionsStore } from "@/stores/sessions";

defineOptions({
  name: "RunningWorkCounter",
});

/**
 * The status bar's count of work left running in the background, in every session: `5 running in 2 sessions`. It
 * opens a list of that work by session, the session you're in first. Hidden when nothing runs.
 */
const router = useRouter();
const sessionsStore = useSessionsStore();
const { sessions, activeSessionId } = storeToRefs(sessionsStore);
const { running, groups, sessionCount, now, isStopping } = useRunningWorkAcrossSessions();
const open = shallowRef(false);

const label = computed(() => {
  const count = running.value.length;
  const where = sessionCount.value === 1 ? "1 session" : `${sessionCount.value} sessions`;
  return `${count} running in ${where}`;
});

const orderedGroups = computed(() => {
  const active = activeSessionId.value;
  return [...groups.value]
    .sort((a, b) => Number(b.sessionId === active) - Number(a.sessionId === active))
    .map((group) => {
      const session = sessions.value.find((candidate) => candidate.session.id === group.sessionId);
      return {
        ...group,
        title: session?.session.title || "Untitled session",
        where: group.sessionId === active ? "this session" : session?.workspaceDisplayName ?? session?.projectName ?? null,
        instanceId: session?.instanceId ?? group.sessionId,
      };
    });
});

function goTo(sessionId: string, instanceId: string, event: MouseEvent): void {
  if (event.metaKey || event.ctrlKey || event.shiftKey || event.button !== 0) return;
  event.preventDefault();
  open.value = false;
  void router.navigate({ to: "/sessions/$id", params: { id: sessionId }, search: { instanceId, parentSessionId: undefined } });
}
</script>

<template>
  <Popover
    v-if="running.length > 0"
    v-model:open="open"
  >
    <PopoverTrigger as-child>
      <button
        type="button"
        class="running-counter"
        data-testid="running-work-counter"
        :title="`${label}: show what runs in the background`"
      >
        <span
          class="running-counter__dot"
          aria-hidden="true"
        />
        {{ label }}
      </button>
    </PopoverTrigger>

    <PopoverContent
      side="top"
      align="end"
      :side-offset="6"
      :collision-padding="8"
      class="w-[460px] max-w-[calc(100vw-16px)] overflow-hidden rounded-card p-0"
      aria-label="Running in the background"
      data-testid="running-work-popover"
    >
      <!-- The popover's own root is portalled, out of reach of these scoped styles; this holds them. -->
      <div class="running-popover">
        <section
          v-for="group in orderedGroups"
          :key="group.sessionId"
          class="running-popover__group"
          data-testid="running-work-group"
        >
          <a
            class="running-popover__session"
            :href="`/sessions/${encodeURIComponent(group.sessionId)}?instanceId=${encodeURIComponent(group.instanceId)}`"
            @click="goTo(group.sessionId, group.instanceId, $event)"
          >
            <span
              class="running-counter__dot"
              aria-hidden="true"
            />
            <span class="running-popover__title">{{ group.title }}</span>
            <span
              v-if="group.where"
              class="running-popover__where"
            >· {{ group.where }}</span>
          </a>
          <ol class="running-popover__list">
            <BackgroundWorkRow
              v-for="item in group.items"
              :key="item.id"
              :item="item"
              :now="now"
              :stopping="isStopping(item.id)"
            />
          </ol>
        </section>
      </div>
    </PopoverContent>
  </Popover>
</template>

<style scoped>
.running-counter {
  display: inline-flex;
  flex-shrink: 0;
  align-items: center;
  gap: 6px;
  height: 18px;
  padding: 0 8px 0 7px;
  border: 0;
  border-radius: 999px;
  background: color-mix(in srgb, var(--running) 14%, transparent);
  color: var(--running);
  font: inherit;
  font-size: 11px;
  font-weight: 500;
  font-variant-numeric: tabular-nums;
  white-space: nowrap;
  cursor: pointer;
  transition: background var(--transition);
}

.running-counter:hover,
.running-counter[data-state="open"] {
  background: color-mix(in srgb, var(--running) 24%, transparent);
}

.running-counter:focus-visible {
  outline: 2px solid var(--running);
  outline-offset: 1px;
}

.running-counter__dot {
  flex-shrink: 0;
  width: 6px;
  height: 6px;
  border-radius: 50%;
  background: var(--running);
}

.running-popover {
  container: background-work / inline-size;
  display: flex;
  flex-direction: column;
  gap: 6px;
  max-height: min(480px, 70vh);
  padding: 6px;
  overflow: auto;
}

.running-popover__session {
  display: flex;
  align-items: center;
  gap: 8px;
  min-width: 0;
  padding: 6px 8px 4px;
  border-radius: 7px;
  color: var(--text);
  font-size: 12.5px;
  font-weight: 600;
  text-decoration: none;
}

.running-popover__session:hover,
.running-popover__session:focus-visible {
  background: color-mix(in srgb, var(--text) 5%, transparent);
}

.running-popover__title {
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.running-popover__where {
  flex-shrink: 0;
  color: var(--muted);
  font-weight: 400;
}

.running-popover__list {
  display: flex;
  flex-direction: column;
  gap: 2px;
  margin: 0;
  padding: 0;
  list-style: none;
}
</style>
