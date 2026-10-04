<script setup lang="ts">
import { computed } from "vue";
import { useRouter } from "@tanstack/vue-router";
import StatusGlyph from "@/components/sessions/StatusGlyph.vue";
import type { RunningWorkItem } from "@/lib/running-work";

/**
 * A running subagent under the session that started it in the session list: its task and a SUBAGENT label. It opens
 * the subagent's own session. It drops off when the subagent finishes; the Agents tab keeps it.
 */
const props = defineProps<{
  item: RunningWorkItem;
  active: boolean;
}>();

const router = useRouter();

const title = computed(() => props.item.label ?? props.item.title);

function open(): void {
  const childId = props.item.childSessionId;
  if (!childId) return;
  void router.navigate({
    to: "/sessions/$id",
    params: { id: childId },
    search: { instanceId: childId, parentSessionId: props.item.sessionId },
  });
}
</script>

<template>
  <button
    type="button"
    class="subagent-row"
    :class="{ 'subagent-row--active': active }"
    :aria-current="active ? 'true' : undefined"
    :title="`${item.title}: ${title}`"
    data-testid="subagent-session-row"
    :data-child-session-id="item.childSessionId"
    @click="open"
  >
    <StatusGlyph
      status="running"
      label="Running"
    />
    <span class="subagent-row__title">{{ title }}</span>
    <span class="subagent-row__kind">subagent</span>
  </button>
</template>

<style scoped>
.subagent-row {
  display: flex;
  align-items: center;
  gap: 9px;
  width: 100%;
  min-width: 0;
  min-height: 28px;
  padding: 0 10px;
  border: 0;
  border-radius: var(--radius-btn);
  background: transparent;
  color: color-mix(in srgb, var(--text) 86%, transparent);
  font: inherit;
  font-size: 13px;
  text-align: left;
  cursor: pointer;
  transition: background var(--transition), color var(--transition);
}

.subagent-row:hover {
  background: color-mix(in srgb, var(--text) 5%, transparent);
  color: var(--text);
}

.subagent-row--active {
  background: var(--card-bg);
  color: var(--text);
}

.subagent-row:focus-visible {
  outline: 2px solid var(--accent);
  outline-offset: -2px;
}

.subagent-row__title {
  flex: 1 1 auto;
  min-width: 0;
  overflow: hidden;
  line-height: 1.3;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.subagent-row__kind {
  flex-shrink: 0;
  color: var(--muted);
  font-size: 10.5px;
  font-weight: 600;
  letter-spacing: 0.04em;
  text-transform: uppercase;
}
</style>
