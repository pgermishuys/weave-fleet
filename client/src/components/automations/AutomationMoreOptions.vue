<script setup lang="ts">
import { useId } from "vue";
import { Ellipsis } from "lucide-vue-next";
import { Popover, PopoverContent, PopoverTrigger } from "@/components/ui/popover";

/** The "…" chip: the automation's name, and whether a run waits out the last one. */

defineProps<{
  /** The name it gets when none is typed, from the prompt's first words. */
  namePlaceholder: string;
  disabled?: boolean;
}>();

const emit = defineEmits<{
  closeAutoFocus: [event: Event];
}>();

const open = defineModel<boolean>("open", { default: false });
const name = defineModel<string>("name", { required: true });
const skip = defineModel<boolean>("skip", { required: true });
const nameId = useId();
const skipId = useId();
</script>

<template>
  <Popover v-model:open="open">
    <PopoverTrigger as-child>
      <button
        type="button"
        class="ns-chip"
        data-testid="automation-more-chip"
        aria-label="More options: name, skipping"
        title="Name, skipping"
        :disabled="disabled"
      >
        <Ellipsis
          class="ns-chip__icon"
          aria-hidden="true"
        />
      </button>
    </PopoverTrigger>
    <PopoverContent
      class="ns-pop ns-more"
      side="top"
      align="end"
      :side-offset="6"
      :collision-padding="8"
      @close-auto-focus="emit('closeAutoFocus', $event)"
    >
      <div class="ns-field">
        <label
          :for="nameId"
          class="ns-field__label"
        >Name</label>
        <input
          :id="nameId"
          v-model="name"
          type="text"
          class="ns-field__input"
          data-testid="automation-name"
          :placeholder="namePlaceholder || 'From the first words'"
          autocomplete="off"
          @keydown.enter.prevent="open = false"
        >
      </div>
      <label
        :for="skipId"
        class="automation-more__check"
      >
        <input
          :id="skipId"
          v-model="skip"
          type="checkbox"
          data-testid="automation-skip"
        >
        <span>
          Skip a run while the last one is still going
          <small>So a slow run never piles up behind itself.</small>
        </span>
      </label>
    </PopoverContent>
  </Popover>
</template>

<style scoped>
.automation-more__check {
  display: flex;
  gap: 8px;
  align-items: flex-start;
  padding: 6px 8px 8px;
  color: var(--text);
  font-size: 12.5px;
  cursor: pointer;
}

.automation-more__check input {
  margin-top: 2px;
  accent-color: var(--accent);
}

.automation-more__check small {
  display: block;
  color: var(--muted);
  font-size: 11.5px;
}
</style>
