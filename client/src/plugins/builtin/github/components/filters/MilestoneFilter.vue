<script setup lang="ts">
import { ref } from "vue";
import { Milestone, Check, Loader2 } from "lucide-vue-next";
import { Popover, PopoverContent, PopoverTrigger } from "@/components/ui/popover";
import { Command, CommandEmpty, CommandGroup, CommandInput, CommandItem, CommandList } from "@/components/ui/command";
import type { GitHubMilestone } from "../../composables/github-types";

const props = defineProps<{
  milestones: GitHubMilestone[];
  isLoading: boolean;
  selected: string | null;
}>();

const emit = defineEmits<{
  select: [milestone: string | null];
}>();

const open = ref(false);

function handleSelect(title: string) {
  emit("select", props.selected === title ? null : title);
  open.value = false;
}
</script>

<template>
  <Popover v-model:open="open">
    <PopoverTrigger as-child>
      <button class="filter-btn">
        <Milestone :size="12" />
        <span>Milestone</span>
        <span v-if="selected" class="filter-badge">1</span>
      </button>
    </PopoverTrigger>
    <PopoverContent class="filter-popover-content" align="start">
      <Command>
        <CommandInput placeholder="Filter milestones…" />
        <CommandList class="filter-list">
          <div v-if="isLoading && milestones.length === 0" class="filter-loading">
            <Loader2 :size="14" class="animate-spin" />
          </div>
          <CommandEmpty v-else-if="!isLoading && milestones.length === 0">No milestones found.</CommandEmpty>
          <CommandGroup>
            <CommandItem
              v-if="selected"
              value="__clear__"
              @select="() => { emit('select', null); open = false; }"
            >
              <span class="filter-clear-text">Clear selection</span>
            </CommandItem>
            <CommandItem
              value="__none__"
              @select="() => handleSelect('none')"
            >
              <div class="filter-item">
                <Check :size="13" :style="{ opacity: selected === 'none' ? 1 : 0 }" class="filter-check" />
                <span class="filter-item-text">No milestone</span>
              </div>
            </CommandItem>
            <CommandItem
              v-for="ms in milestones"
              :key="ms.number"
              :value="ms.title"
              @select="() => handleSelect(ms.title)"
            >
              <div class="filter-item">
                <Check :size="13" :style="{ opacity: selected === ms.title ? 1 : 0 }" class="filter-check" />
                <div class="milestone-info">
                  <span class="filter-item-text">{{ ms.title }}</span>
                  <span class="milestone-meta">{{ ms.open_issues }} open · {{ ms.closed_issues }} closed</span>
                </div>
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
  width: 224px;
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

.filter-clear-text {
  font-size: 13px;
  color: var(--muted);
}

.filter-item {
  display: flex;
  align-items: center;
  gap: 8px;
  flex: 1;
  min-width: 0;
}

.filter-check {
  flex-shrink: 0;
}

.milestone-info {
  display: flex;
  flex-direction: column;
  min-width: 0;
}

.filter-item-text {
  font-size: 13px;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.milestone-meta {
  font-size: 10px;
  color: var(--muted);
}
</style>
