<script setup lang="ts">
import { useRouter } from "@tanstack/vue-router";
import { Zap } from "lucide-vue-next";
import { useAutomationsNav } from "@/composables/use-automations-nav";
import type { WorkflowRunStartedBy } from "@/lib/workflows";

/**
 * "Started by <automation>": a run an automation started, with a link that opens the automation. Inside something
 * that's already a button (a Library row), it's plain text.
 */
const props = defineProps<{ startedBy: WorkflowRunStartedBy; plain?: boolean }>();

const router = useRouter();
const { setActiveAutomation } = useAutomationsNav();

function open(event: Event): void {
  event.stopPropagation();
  setActiveAutomation(props.startedBy.automationId);
  void router.navigate({ to: "/automations" });
}
</script>

<template>
  <span
    v-if="plain"
    class="wf-started-by wf-started-by--plain"
    data-testid="workflow-started-by"
  >
    <Zap aria-hidden="true" />
    <span>Started by <b>{{ startedBy.automationName }}</b></span>
  </span>
  <button
    v-else
    type="button"
    class="wf-started-by"
    data-testid="workflow-started-by"
    :title="`Open the ${startedBy.automationName} automation`"
    @click="open"
  >
    <Zap aria-hidden="true" />
    <span>Started by <b>{{ startedBy.automationName }}</b></span>
  </button>
</template>

<style scoped>
.wf-started-by {
  display: inline-flex;
  min-width: 0;
  align-items: center;
  gap: 4px;
  border: 0;
  padding: 0;
  background: none;
  color: var(--muted);
  font: inherit;
  font-size: 11.5px;
  cursor: pointer;
}

.wf-started-by span {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.wf-started-by b {
  color: var(--text);
  font-weight: 500;
}

.wf-started-by--plain {
  cursor: inherit;
}

.wf-started-by:not(.wf-started-by--plain):hover span {
  text-decoration: underline;
  text-underline-offset: 3px;
}

.wf-started-by:focus-visible {
  outline: 2px solid var(--accent);
  outline-offset: 2px;
}

.wf-started-by svg {
  width: 12px;
  height: 12px;
  flex-shrink: 0;
}
</style>
