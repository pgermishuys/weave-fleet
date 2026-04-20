<script setup lang="ts">
import { computed } from "vue";
import SelectorDropdown from "@/components/session/SelectorDropdown.vue";
import type { AgentOption } from "@/composables/use-agents";

const props = defineProps<{
  agents: readonly AgentOption[];
}>();

const selectedAgentId = defineModel<string>({ required: true });

const items = computed(() => {
  return [
    {
      id: "",
      label: "Default",
      description: "Use the session default agent",
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
  />
</template>
