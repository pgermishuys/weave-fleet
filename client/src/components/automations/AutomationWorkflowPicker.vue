<script setup lang="ts">
import { computed } from "vue";
import { AlertTriangle, Check, ChevronDown, Workflow as WorkflowIcon } from "lucide-vue-next";
import { DropdownMenuItem } from "reka-ui";
import { DropdownMenu, DropdownMenuContent, DropdownMenuTrigger } from "@/components/ui/dropdown-menu";
import type { Workflow } from "@/lib/workflows";

/** The workflow an automation runs: the Library for its folder, built into Fleet first, then the repo's own. */

const props = defineProps<{
  workflowId: string | null;
  /** The Library for the chosen folder; null until it has loaded. */
  workflows: readonly Workflow[] | null;
  isLoading?: boolean;
  /** Why the Library couldn't load, or why there's none to show (no repository picked). */
  note?: string | null;
  disabled?: boolean;
}>();

const emit = defineEmits<{
  "update:workflowId": [workflowId: string];
  closeAutoFocus: [event: Event];
}>();

const builtIns = computed(() => props.workflows?.filter((workflow) => workflow.builtIn) ?? []);
const repoWorkflows = computed(() => props.workflows?.filter((workflow) => !workflow.builtIn) ?? []);

/** A saved workflow the Library no longer lists still shows by its id, so the chip never goes blank. */
const chipLabel = computed(() => {
  if (!props.workflowId) return "Pick a workflow";
  return props.workflows?.find((workflow) => workflow.id === props.workflowId)?.name
    ?? props.workflowId.replace(/^(builtin|repo):/, "");
});
</script>

<template>
  <DropdownMenu :modal="false">
    <DropdownMenuTrigger as-child>
      <button
        type="button"
        class="ns-chip"
        data-testid="automation-workflow-chip"
        :disabled="disabled"
      >
        <WorkflowIcon
          class="ns-chip__icon"
          aria-hidden="true"
        />
        <span class="ns-chip__label">{{ chipLabel }}</span>
        <ChevronDown
          class="ns-chip__chevron"
          aria-hidden="true"
        />
      </button>
    </DropdownMenuTrigger>
    <DropdownMenuContent
      class="ns-pop"
      side="top"
      align="start"
      :side-offset="6"
      :collision-padding="8"
      @close-auto-focus="emit('closeAutoFocus', $event)"
    >
      <p
        v-if="note"
        class="ns-pop__label"
      >
        {{ note }}
      </p>
      <p
        v-else-if="isLoading && !workflows"
        class="ns-pop__label"
      >
        Loading the workflows…
      </p>
      <template
        v-for="group in [
          { label: 'Built into Fleet', items: builtIns },
          { label: 'This repo · .weave/workflows', items: repoWorkflows },
        ]"
        :key="group.label"
      >
        <template v-if="group.items.length > 0">
          <div class="ns-pop__label">
            {{ group.label }}
          </div>
          <DropdownMenuItem
            v-for="workflow in group.items"
            :key="workflow.id"
            class="ns-option"
            :disabled="workflow.errors.length > 0"
            :data-testid="`automation-workflow-${workflow.id}`"
            @select="emit('update:workflowId', workflow.id)"
          >
            <AlertTriangle
              v-if="workflow.errors.length > 0"
              class="ns-option__icon"
              aria-hidden="true"
            />
            <WorkflowIcon
              v-else
              class="ns-option__icon"
              aria-hidden="true"
            />
            <span class="ns-option__text">
              <span class="ns-option__title">{{ workflow.name }}</span>
              <span class="ns-option__detail">{{ workflow.errors.length > 0 ? "The file has errors; fix it in the Library first" : workflow.file ?? workflow.description }}</span>
            </span>
            <Check
              v-if="workflow.id === workflowId"
              class="ns-option__check"
              aria-hidden="true"
            />
          </DropdownMenuItem>
        </template>
      </template>
    </DropdownMenuContent>
  </DropdownMenu>
</template>
