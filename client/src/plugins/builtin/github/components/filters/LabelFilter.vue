<script setup lang="ts">
import { ref } from "vue";
import { Tag, Check, Loader2 } from "lucide-vue-next";
import { Popover, PopoverContent, PopoverTrigger } from "@/components/ui/popover";
import { Command, CommandEmpty, CommandGroup, CommandInput, CommandItem, CommandList } from "@/components/ui/command";
import type { GitHubLabel } from "../../composables/github-types";

defineProps<{
  labels: GitHubLabel[];
  isLoading: boolean;
  selected: string[];
}>();

const emit = defineEmits<{
  toggle: [label: string];
}>();

const open = ref(false);
</script>

<template>
  <Popover v-model:open="open">
    <PopoverTrigger as-child>
      <button class="filter-btn">
        <Tag :size="12" />
        <span>Label</span>
        <span
          v-if="selected.length > 0"
          class="filter-badge"
        >{{ selected.length }}</span>
      </button>
    </PopoverTrigger>
    <PopoverContent
      class="filter-popover-content"
      align="start"
    >
      <Command>
        <CommandInput placeholder="Filter labels…" />
        <CommandList class="filter-list">
          <div
            v-if="isLoading && labels.length === 0"
            class="filter-loading"
          >
            <Loader2
              :size="14"
              class="animate-spin"
            />
          </div>
          <CommandEmpty v-else-if="!isLoading && labels.length === 0">
            No labels found.
          </CommandEmpty>
          <CommandGroup>
            <CommandItem
              v-for="label in labels"
              :key="label.name"
              :value="label.name"
              @select="() => emit('toggle', label.name)"
            >
              <div class="filter-item">
                <div :class="['filter-checkbox', selected.includes(label.name) && 'filter-checkbox--checked']">
                  <Check
                    v-if="selected.includes(label.name)"
                    :size="10"
                  />
                </div>
                <span
                  class="label-dot"
                  :style="{ backgroundColor: `#${label.color}` }"
                />
                <span class="filter-item-text">{{ label.name }}</span>
              </div>
            </CommandItem>
          </CommandGroup>
        </CommandList>
      </Command>
    </PopoverContent>
  </Popover>
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

.filter-badge {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  min-width: 16px;
  height: 16px;
  padding: 0 4px;
  border-radius: 999px;
  background: var(--accent-muted, rgba(99, 102, 241, 0.2));
  color: var(--accent);
  font-size: 10px;
  font-weight: 600;
}

.filter-popover-content {
  width: 256px;
  padding: 0;
}

.filter-list {
  max-height: 224px;
}

.filter-loading {
  display: flex;
  justify-content: center;
  padding: 12px;
  color: var(--muted);
}

.filter-item {
  display: flex;
  align-items: center;
  gap: 8px;
  flex: 1;
  min-width: 0;
}

.filter-checkbox {
  display: flex;
  align-items: center;
  justify-content: center;
  width: 14px;
  height: 14px;
  flex-shrink: 0;
  border-radius: 3px;
  border: 1px solid var(--border);
}

.filter-checkbox--checked {
  background: var(--accent);
  border-color: var(--accent);
  color: white;
}

.label-dot {
  width: 10px;
  height: 10px;
  border-radius: 50%;
  flex-shrink: 0;
}

.filter-item-text {
  font-size: 13px;
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
</style>
