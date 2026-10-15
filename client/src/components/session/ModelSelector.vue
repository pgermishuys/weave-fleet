<script setup lang="ts">
import { computed } from "vue";
import SelectorDropdown from "@/components/session/SelectorDropdown.vue";
import type { ModelOption } from "@/composables/use-models";
import { modelFromKey } from "@/lib/agent-model-choice";

const props = withDefaults(
  defineProps<{
    models: readonly ModelOption[];
    /** What "Default" says on the chip and in the list, e.g. "Default (Claude Opus 4.7)". */
    defaultLabel?: string;
    defaultDescription?: string;
    disabled?: boolean;
    testId?: string;
  }>(),
  {
    defaultLabel: "Default",
    defaultDescription: "Use the session default model",
    disabled: false,
    testId: undefined,
  },
);

const selectedModelId = defineModel<string>({ required: true });

/** What picking HydraFusion means for the bill; the Settings switch says the same. */
const HYDRAFUSION_NOTE = "HydraFusion runs several models per prompt; cost varies per prompt.";
const isHydraFusion = (text: string | undefined): boolean => /hydrafusion/i.test(text ?? "");

const items = computed(() => {
  return [
    {
      id: "",
      label: props.defaultLabel,
      description: isHydraFusion(props.defaultLabel) ? HYDRAFUSION_NOTE : props.defaultDescription,
    },
    ...props.models.map((model) => ({
      id: model.selectionKey,
      label: model.name,
      description: isHydraFusion(model.id) ? HYDRAFUSION_NOTE : model.description,
      meta: model.provider,
    })),
  ];
});

/** A model picked that the list no longer has (its provider was signed out) still shows by its id. */
const placeholder = computed(() => modelFromKey(selectedModelId.value)?.modelID ?? "Select model");
</script>

<template>
  <SelectorDropdown
    v-model="selectedModelId"
    label="Model selector"
    :placeholder="placeholder"
    :items="items"
    :disabled="disabled"
    :test-id="testId"
  />
</template>
