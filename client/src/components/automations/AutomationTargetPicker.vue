<script setup lang="ts">
import { Check, ChevronDown, MessageSquare, Workflow as WorkflowIcon } from "lucide-vue-next";
import { DropdownMenuItem } from "reka-ui";
import { DropdownMenu, DropdownMenuContent, DropdownMenuTrigger } from "@/components/ui/dropdown-menu";

/** What each run starts: a session (the targets automations always had) or a workflow from the Library. */

defineProps<{
  kind: "session" | "workflow";
  /** Workflows are on in Settings; with them off, only an automation that already runs one shows the choice. */
  workflowsEnabled: boolean;
  disabled?: boolean;
}>();

const emit = defineEmits<{
  "update:kind": [kind: "session" | "workflow"];
  closeAutoFocus: [event: Event];
}>();
</script>

<template>
  <DropdownMenu :modal="false">
    <DropdownMenuTrigger as-child>
      <button
        type="button"
        class="ns-chip"
        data-testid="automation-target-chip"
        :disabled="disabled"
      >
        <WorkflowIcon
          v-if="kind === 'workflow'"
          class="ns-chip__icon"
          aria-hidden="true"
        />
        <MessageSquare
          v-else
          class="ns-chip__icon"
          aria-hidden="true"
        />
        <span class="ns-chip__hint">Runs</span>
        <span class="ns-chip__label">{{ kind === "workflow" ? "a workflow" : "a session" }}</span>
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
      <DropdownMenuItem
        class="ns-option"
        data-testid="automation-target-session"
        @select="emit('update:kind', 'session')"
      >
        <MessageSquare
          class="ns-option__icon"
          aria-hidden="true"
        />
        <span class="ns-option__text">
          <span class="ns-option__title">A session</span>
          <span class="ns-option__detail">the agent gets your message</span>
        </span>
        <Check
          v-if="kind === 'session'"
          class="ns-option__check"
          aria-hidden="true"
        />
      </DropdownMenuItem>
      <DropdownMenuItem
        v-if="workflowsEnabled || kind === 'workflow'"
        class="ns-option"
        data-testid="automation-target-workflow"
        @select="emit('update:kind', 'workflow')"
      >
        <WorkflowIcon
          class="ns-option__icon"
          aria-hidden="true"
        />
        <span class="ns-option__text">
          <span class="ns-option__title">A workflow</span>
          <span class="ns-option__detail">a workflow from the Library, with your message as its request</span>
        </span>
        <Check
          v-if="kind === 'workflow'"
          class="ns-option__check"
          aria-hidden="true"
        />
      </DropdownMenuItem>
    </DropdownMenuContent>
  </DropdownMenu>
</template>
