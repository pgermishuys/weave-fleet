<script setup lang="ts">
import { computed } from "vue";
import { useRouter } from "@tanstack/vue-router";
import { ArrowUpRight, Bot, Check } from "lucide-vue-next";
import StatusGlyph from "@/components/sessions/StatusGlyph.vue";
import type { ToolCardDelegation } from "@/components/session/activity-stream-tool-card";

const props = defineProps<{
  delegation: ToolCardDelegation;
}>();

const router = useRouter();

const isWorking = computed(() => props.delegation.status === "running" || props.delegation.status === "pending");
// The sub-agent's question holds it up, and its session is where you answer.
const needsInput = computed(() => isWorking.value && props.delegation.needsInput === true);
const statusWord = computed(() => {
  if (needsInput.value) return "Needs input";
  switch (props.delegation.status) {
    case "completed": return "Done";
    case "error": return "Failed";
    case "cancelled": return "Cancelled";
    case "pending": return "Starting";
    default: return "Working";
  }
});

function handleClick(event: MouseEvent): void {
  // A modified click opens the child elsewhere, as a link would.
  if (event.metaKey || event.ctrlKey || event.shiftKey || event.button !== 0) return;
  event.preventDefault();
  const { childSessionId, childInstanceId, parentSessionId } = props.delegation;
  void router.navigate({
    to: "/sessions/$id",
    params: { id: childSessionId },
    search: { instanceId: childInstanceId, parentSessionId },
  });
}
</script>

<template>
  <!-- A sub-agent's task is one row: who, what, how it's going, and a way into its session. -->
  <a
    class="agent-task"
    :class="[`agent-task--${delegation.status}`, { 'agent-task--needs-input': needsInput }]"
    :href="delegation.href"
    :title="needsInput ? `Open ${delegation.task} to answer its question` : `Open ${delegation.task}`"
    data-testid="delegation-link"
    @click="handleClick"
  >
    <Bot
      class="agent-task__icon"
      aria-hidden="true"
    />
    <span
      v-if="delegation.agent"
      class="agent-task__agent"
    >{{ delegation.agent }}</span>
    <span
      class="agent-task__task"
      data-testid="delegation-link-title"
    >{{ delegation.task }}</span>
    <span class="agent-task__end">
      <span
        class="agent-task__open"
        aria-hidden="true"
      >
        Open
        <ArrowUpRight />
      </span>
      <span
        class="agent-task__status"
        data-testid="delegation-link-status"
      >{{ statusWord }}</span>
      <StatusGlyph
        v-if="needsInput"
        status="waiting_input"
        :label="statusWord"
      />
      <StatusGlyph
        v-else-if="isWorking"
        status="active"
        :label="statusWord"
      />
      <Check
        v-else-if="delegation.status === 'completed'"
        class="agent-task__done"
        aria-hidden="true"
      />
      <StatusGlyph
        v-else-if="delegation.status === 'error'"
        status="error"
        :label="statusWord"
      />
    </span>
  </a>
</template>

<style scoped>
/* Sized and spaced like ToolCard's header, so it sits in the tool list as one of the rows. */
.agent-task {
  display: flex;
  align-items: center;
  gap: 9px;
  min-width: 0;
  min-height: 30px;
  padding: 0 8px;
  border-radius: calc(var(--radius-btn) - 2px);
  color: var(--text);
  font-size: 13px;
  text-decoration: none;
  transition: background var(--transition);
}

.agent-task:hover {
  background: color-mix(in srgb, var(--text) 5%, transparent);
}

.agent-task:focus-visible {
  outline: 2px solid var(--accent);
  outline-offset: -2px;
}

.agent-task__icon {
  width: 14px;
  height: 14px;
  flex-shrink: 0;
  color: color-mix(in srgb, var(--muted) 75%, transparent);
}

.agent-task__agent {
  flex-shrink: 0;
  font-weight: 600;
}

.agent-task__task {
  flex: 1;
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.agent-task__end {
  display: flex;
  flex-shrink: 0;
  align-items: center;
  gap: 8px;
  margin-left: auto;
}

.agent-task__open {
  display: inline-flex;
  align-items: center;
  gap: 2px;
  color: var(--accent);
  font-size: 12px;
  font-weight: 500;
  opacity: 0;
  transition: opacity var(--transition);
}

.agent-task__open svg {
  width: 12px;
  height: 12px;
}

.agent-task:hover .agent-task__open,
.agent-task:focus-visible .agent-task__open {
  opacity: 1;
}

.agent-task__status {
  color: var(--muted);
  font-size: 12px;
}

.agent-task--needs-input .agent-task__status {
  color: var(--status-waiting);
  font-weight: 500;
}

.agent-task--error .agent-task__status {
  color: var(--error);
}

.agent-task__done {
  width: 13px;
  height: 13px;
  color: var(--running);
}
</style>
