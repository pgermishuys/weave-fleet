<script setup lang="ts">
import { computed } from "vue";
import SelectorDropdown from "@/components/session/SelectorDropdown.vue";
import type { ModelOption } from "@/composables/use-models";

const props = defineProps<{
  models: readonly ModelOption[];
}>();

const selectedModelId = defineModel<string>({ required: true });

const items = computed(() => {
  return [
    {
      id: "",
      label: "Default",
      description: "Use the session default model",
    },
    ...props.models.map((model) => ({
      id: model.id,
      label: model.name,
      description: model.description,
      meta: model.provider,
    })),
  ];
});
</script>

<template>
  <SelectorDropdown
    v-model="selectedModelId"
    label="Model selector"
    placeholder="Select model"
    :items="items"
  />
</template>
