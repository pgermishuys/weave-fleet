<script setup lang="ts">
import { computed, shallowRef, watch } from "vue";
import { Check, Search, Star } from "lucide-vue-next";
import BottomSheet from "@/components/phone/BottomSheet.vue";
import type { ModelOption } from "@/composables/use-models";
import { groupModels } from "@/lib/model-picker-groups";
import { modelPickerKey, useModelPickerStore } from "@/stores/model-picker";

/** The model chip's sheet: PhoneChipSheet's rows, grouped as on the desktop, with a star on every row. */
const props = defineProps<{ open: boolean; models: readonly ModelOption[]; selected: string; harnessType?: string }>();
const emit = defineEmits<{ (event: "pick", id: string): void; (event: "close"): void }>();

/** Past this many models the sheet gets a filter, and the list reads as one long run to search. */
const FILTER_ABOVE = 8;

const picker = useModelPickerStore();
const query = shallowRef("");
watch(() => props.open, (open) => {
  if (!open) query.value = "";
});

const harness = computed(() => props.harnessType ?? "");
const keyOf = (model: ModelOption): string => modelPickerKey(harness.value, model.providerId, model.id);

const sections = computed(() => {
  const text = query.value.trim().toLowerCase();
  if (text) {
    const found = props.models.filter((model) => `${model.name} ${model.provider} ${model.id}`.toLowerCase().includes(text));
    return [{ title: undefined, items: found }];
  }
  return groupModels(props.models, harness.value, picker.favourites, picker.recent, true);
});
</script>

<template>
  <BottomSheet
    :open="open"
    label="Model"
    title="Model"
    @close="emit('close')"
  >
    <div
      v-if="models.length > FILTER_ABOVE"
      class="ph-sheet__pad"
    >
      <label class="ph-field">
        <Search aria-hidden="true" />
        <input
          v-model="query"
          class="phone-composer-input"
          type="search"
          placeholder="Find a model"
          aria-label="Find a model"
          autocapitalize="off"
          autocomplete="off"
          spellcheck="false"
        >
      </label>
    </div>
    <template
      v-for="(section, index) in sections"
      :key="index"
    >
      <p
        v-if="section.title"
        class="pms__head"
      >
        {{ section.title }}
      </p>
      <div
        v-if="section.items.length"
        class="ph-card"
      >
        <div
          v-for="model in section.items"
          :key="model.selectionKey"
          class="pms__row"
        >
          <button
            type="button"
            class="ph-set"
            :aria-pressed="model.selectionKey === selected"
            @click="emit('pick', model.selectionKey); emit('close')"
          >
            <span class="ph-set__main">
              <span class="ph-set__t">{{ model.name }}</span>
              <span class="ph-set__s">{{ model.provider }}</span>
            </span>
            <Check
              v-if="model.selectionKey === selected"
              class="ph-set__check"
              aria-hidden="true"
            />
          </button>
          <button
            v-if="harness"
            type="button"
            class="pms__star"
            :class="{ 'pms__star--on': picker.isFavourite(keyOf(model)) }"
            :aria-label="picker.isFavourite(keyOf(model)) ? 'Remove from favourites' : 'Add to favourites'"
            :aria-pressed="picker.isFavourite(keyOf(model))"
            @click="picker.toggleFavourite(keyOf(model))"
          >
            <Star aria-hidden="true" />
          </button>
        </div>
      </div>
    </template>
    <p
      v-if="sections.every((section) => section.items.length === 0)"
      class="ph-foot"
    >
      No matching models
    </p>
  </BottomSheet>
</template>

<style scoped>
.pms__head {
  margin: 14px 28px 6px;
  color: var(--muted);
  font-size: var(--ph-t-meta);
  font-weight: 600;
  letter-spacing: 0.06em;
  text-transform: uppercase;
}

.pms__row {
  display: flex;
  align-items: center;
}

.pms__row + .pms__row {
  border-top: 1px solid var(--border);
}

.pms__row .ph-set {
  flex: 1;
  min-width: 0;
}

.pms__star {
  display: flex;
  flex: none;
  align-items: center;
  justify-content: center;
  width: 40px;
  height: 40px;
  margin-right: 6px;
  border: 0;
  background: transparent;
  color: var(--muted);
}

.pms__star svg {
  width: 18px;
  height: 18px;
}

.pms__star--on {
  color: var(--idle);
}

.pms__star--on svg {
  fill: currentColor;
}
</style>
