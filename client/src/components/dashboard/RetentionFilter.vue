<script setup lang="ts">
import { computed } from "vue";
import { Check, ChevronDown } from "lucide-vue-next";
import { Button } from "@/components/ui/button";
import {
  DropdownMenu,
  DropdownMenuContent,
  DropdownMenuItem,
  DropdownMenuTrigger,
} from "@/components/ui/dropdown-menu";

type RetentionFilterValue = "active" | "archived" | "all";

interface Props {
  modelValue: RetentionFilterValue;
}

interface Emits {
  "update:modelValue": [value: RetentionFilterValue];
}

interface FilterOption {
  key: RetentionFilterValue;
  label: string;
}

const props = defineProps<Props>();
const emit = defineEmits<Emits>();

const options: readonly FilterOption[] = [
  { key: "active", label: "Active" },
  { key: "archived", label: "Archived" },
  { key: "all", label: "All" },
];

const selectedLabel = computed(() => {
  return options.find((option) => option.key === props.modelValue)?.label ?? "Active";
});

function selectOption(value: RetentionFilterValue): void {
  emit("update:modelValue", value);
}
</script>

<template>
  <DropdownMenu>
    <DropdownMenuTrigger as-child>
      <Button
        data-testid="retention-filter-trigger"
        variant="ghost"
        size="sm"
        class="h-7 justify-between gap-1.5 text-[12.5px] font-normal text-muted hover:text-text"
      >
        <span>Show: {{ selectedLabel }}</span>
        <ChevronDown class="h-3.5 w-3.5" />
      </Button>
    </DropdownMenuTrigger>

    <DropdownMenuContent
      align="end"
      class="w-48"
    >
      <DropdownMenuItem
        v-for="option in options"
        :key="option.key"
        :data-testid="`retention-filter-option-${option.key}`"
        class="justify-between"
        @select="selectOption(option.key)"
      >
        <span>{{ option.label }}</span>
        <Check
          v-if="props.modelValue === option.key"
          class="h-4 w-4"
        />
      </DropdownMenuItem>
    </DropdownMenuContent>
  </DropdownMenu>
</template>
