<script setup lang="ts">
import { computed } from "vue";
import { ArrowUpDown } from "lucide-vue-next";
import { DropdownMenu, DropdownMenuContent, DropdownMenuRadioGroup, DropdownMenuRadioItem, DropdownMenuTrigger } from "@/components/ui/dropdown-menu";

const props = defineProps<{
  sort: "created" | "updated" | "comments";
  direction: "asc" | "desc";
}>();

const emit = defineEmits<{
  change: [sort: "created" | "updated" | "comments", direction: "asc" | "desc"];
}>();

type SortOption = {
  value: string;
  label: string;
  sort: "created" | "updated" | "comments";
  direction: "asc" | "desc";
};

const SORT_OPTIONS: SortOption[] = [
  { value: "updated-desc", label: "Recently updated", sort: "updated", direction: "desc" },
  { value: "updated-asc", label: "Least recently updated", sort: "updated", direction: "asc" },
  { value: "created-desc", label: "Newest", sort: "created", direction: "desc" },
  { value: "created-asc", label: "Oldest", sort: "created", direction: "asc" },
  { value: "comments-desc", label: "Most commented", sort: "comments", direction: "desc" },
];

const currentValue = computed(() => `${props.sort}-${props.direction}`);
const currentLabel = computed(
  () => SORT_OPTIONS.find((o) => o.value === currentValue.value)?.label ?? "Sort"
);

function handleChange(value: unknown) {
  if (typeof value !== "string") return;
  const option = SORT_OPTIONS.find((o) => o.value === value);
  if (option) {
    emit("change", option.sort, option.direction);
  }
}
</script>

<template>
  <DropdownMenu>
    <DropdownMenuTrigger as-child>
      <button class="filter-btn">
        <ArrowUpDown :size="12" />
        <span>{{ currentLabel }}</span>
      </button>
    </DropdownMenuTrigger>
    <DropdownMenuContent
      align="end"
      class="sort-menu"
    >
      <DropdownMenuRadioGroup
        :model-value="currentValue"
        @update:model-value="handleChange"
      >
        <DropdownMenuRadioItem
          v-for="option in SORT_OPTIONS"
          :key="option.value"
          :value="option.value"
        >
          {{ option.label }}
        </DropdownMenuRadioItem>
      </DropdownMenuRadioGroup>
    </DropdownMenuContent>
  </DropdownMenu>
</template>

<style scoped>
.filter-btn {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  padding: 2px 8px;
  height: 28px;
  border-radius: 6px;
  border: none;
  background: transparent;
  color: var(--text);
  font-size: 11px;
  cursor: pointer;
}

.filter-btn:hover {
  background: var(--sidebar-item-hover);
}

.sort-menu {
  width: 192px;
}
</style>
