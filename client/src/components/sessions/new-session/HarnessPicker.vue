<script setup lang="ts">
import { computed } from "vue";
import { Check, ChevronDown } from "lucide-vue-next";
import { DropdownMenuItem } from "reka-ui";
import type { HarnessInfo } from "@/api/client";
import { DropdownMenu, DropdownMenuContent, DropdownMenuTrigger } from "@/components/ui/dropdown-menu";

const props = defineProps<{
  harnesses: readonly HarnessInfo[];
  disabled?: boolean;
}>();

const emit = defineEmits<{
  closeAutoFocus: [event: Event];
}>();

const harnessType = defineModel<string>({ required: true });

const selectedName = computed(() => {
  return props.harnesses.find((harness) => harness.type === harnessType.value)?.displayName ?? harnessType.value;
});
</script>

<template>
  <DropdownMenu :modal="false">
    <DropdownMenuTrigger as-child>
      <button
        type="button"
        class="ns-chip"
        data-testid="new-session-harness"
        aria-label="Harness"
        :disabled="disabled"
      >
        <span class="ns-chip__label">{{ selectedName }}</span>
        <ChevronDown
          class="ns-chip__chevron"
          aria-hidden="true"
        />
      </button>
    </DropdownMenuTrigger>

    <DropdownMenuContent
      class="ns-pop ns-harness"
      side="top"
      align="start"
      :side-offset="6"
      :collision-padding="8"
      @close-auto-focus="emit('closeAutoFocus', $event)"
    >
      <div class="ns-pop__label">
        Harness
      </div>
      <DropdownMenuItem
        v-for="harness in harnesses"
        :key="harness.type"
        class="ns-option ns-harness__option"
        @select="harnessType = harness.type"
      >
        <span class="ns-option__text">
          <span class="ns-option__title">{{ harness.displayName }}</span>
        </span>
        <Check
          v-if="harness.type === harnessType"
          class="ns-option__check"
          aria-hidden="true"
        />
      </DropdownMenuItem>
    </DropdownMenuContent>
  </DropdownMenu>
</template>

<style>
.ns-harness {
  width: 220px;
}

.ns-harness__option {
  grid-template-columns: minmax(0, 1fr) 14px;
}
</style>
