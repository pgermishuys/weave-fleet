<script setup lang="ts">
import { computed } from "vue";
import SelectorDropdown from "@/components/session/SelectorDropdown.vue";
import type { AgentOption } from "@/composables/use-agents";

const props = withDefaults(
  defineProps<{
    agents: readonly AgentOption[];
    /** What "Default" says on the chip and in the list, e.g. "Default (loom)". */
    defaultLabel?: string;
    defaultDescription?: string;
    disabled?: boolean;
    testId?: string;
  }>(),
  {
    defaultLabel: "Default",
    defaultDescription: "Use the session default agent",
    disabled: false,
    testId: undefined,
  },
);

const selectedAgentId = defineModel<string>({ required: true });

const items = computed(() => {
  return [
    {
      id: "",
      label: props.defaultLabel,
      description: props.defaultDescription,
    },
    ...props.agents.map((agent) => ({
      id: agent.id,
      label: agent.name,
      description: agent.description,
    })),
  ];
});
</script>

<template>
  <SelectorDropdown
    v-model="selectedAgentId"
    label="Agent selector"
    placeholder="Select agent"
    :items="items"
    :disabled="disabled"
    :test-id="testId"
  />
</template>
