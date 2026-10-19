<script setup lang="ts">
import AllowedRulesList from "@/components/session/AllowedRulesList.vue";
import { usePermissionLevel } from "@/composables/use-permission-level";
import type { AllowedRule } from "@/lib/tools";

defineOptions({
  name: "AllowedRulesPanel",
});

/**
 * The Allowed list with the level its session runs at, read from Settings. Only mounted once the list is opened, so a
 * session never asks for the preferences just because it has a chip.
 */
const props = defineProps<{
  rules: readonly AllowedRule[];
  harnessType?: string;
}>();

const emit = defineEmits<{
  remove: [ruleId: string];
  removeAll: [];
}>();

const level = usePermissionLevel(() => props.harnessType);
</script>

<template>
  <AllowedRulesList
    :rules="rules"
    :level="level"
    @remove="emit('remove', $event)"
    @remove-all="emit('removeAll')"
  />
</template>
