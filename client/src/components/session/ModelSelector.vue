<script setup lang="ts">
import { computed } from "vue";
import SelectorDropdown from "@/components/session/SelectorDropdown.vue";
import type { ModelOption } from "@/composables/use-models";
import { modelFromKey } from "@/lib/agent-model-choice";
import { groupModels } from "@/lib/model-picker-groups";
import { modelPickerKey, useModelPickerStore } from "@/stores/model-picker";

const props = withDefaults(
  defineProps<{
    models: readonly ModelOption[];
    /** What "Default" says on the chip and in the list, e.g. "Default (Claude Opus 4.7)". */
    defaultLabel?: string;
    defaultDescription?: string;
    /** Which harness the models belong to; without it there are no stars or groups. */
    harnessType?: string;
    /** Recent is for pickers where a model is sent with; settings pickers show favourites only. */
    showRecent?: boolean;
    disabled?: boolean;
    testId?: string;
  }>(),
  {
    defaultLabel: "Default",
    defaultDescription: "Use the session default model",
    harnessType: undefined,
    showRecent: true,
    disabled: false,
    testId: undefined,
  },
);

const picker = useModelPickerStore();

const selectedModelId = defineModel<string>({ required: true });

/** What picking HydraFusion means for the bill; the Settings switch says the same. */
const HYDRAFUSION_NOTE = "HydraFusion runs several models per prompt; cost varies per prompt.";
const isHydraFusion = (text: string | undefined): boolean => /hydrafusion/i.test(text ?? "");

const defaultItem = computed(() => ({
  id: "",
  label: props.defaultLabel,
  description: isHydraFusion(props.defaultLabel) ? HYDRAFUSION_NOTE : props.defaultDescription,
}));

const modelsByKey = computed(() => new Map(props.models.map((model) => [model.selectionKey, model])));

function itemFor(model: ModelOption) {
  return {
    id: model.selectionKey,
    label: model.name,
    description: isHydraFusion(model.id) ? HYDRAFUSION_NOTE : model.description,
    meta: model.provider,
    starred: props.harnessType
      ? picker.favourites.includes(modelPickerKey(props.harnessType, model.providerId, model.id))
      : undefined,
  };
}

const items = computed(() => [defaultItem.value, ...props.models.map(itemFor)]);

const sections = computed(() => {
  if (!props.harnessType) return undefined;
  const groups = groupModels(props.models, props.harnessType, picker.favourites, picker.recent, props.showRecent);
  return [
    { items: [defaultItem.value] },
    ...groups.map((group) => ({ ...group, items: group.items.map(itemFor) })),
  ];
});

function toggleStar(selectionKey: string): void {
  const model = modelsByKey.value.get(selectionKey);
  if (model && props.harnessType) picker.toggleFavourite(modelPickerKey(props.harnessType, model.providerId, model.id));
}

/** A model picked that the list no longer has (its provider was signed out) still shows by its id. */
const placeholder = computed(() => modelFromKey(selectedModelId.value)?.modelID ?? "Select model");
</script>

<template>
  <SelectorDropdown
    v-model="selectedModelId"
    label="Model selector"
    :placeholder="placeholder"
    :items="items"
    :sections="sections"
    :disabled="disabled"
    :test-id="testId"
    @toggle-star="toggleStar"
  />
</template>
