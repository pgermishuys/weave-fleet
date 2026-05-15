<script setup lang="ts">
import { ref } from "vue";
import { User, Check, Loader2 } from "lucide-vue-next";
import { Popover, PopoverContent, PopoverTrigger } from "@/components/ui/popover";
import { Command, CommandEmpty, CommandGroup, CommandInput, CommandItem, CommandList } from "@/components/ui/command";
import type { GitHubAssignee } from "../../composables/github-types";

const props = defineProps<{
  users: GitHubAssignee[];
  isLoading: boolean;
  selected: string | null;
}>();

const emit = defineEmits<{
  select: [author: string | null];
}>();

const open = ref(false);

function handleSelect(login: string) {
  emit("select", props.selected === login ? null : login);
  open.value = false;
}
</script>

<template>
  <Popover v-model:open="open">
    <PopoverTrigger as-child>
      <button class="filter-btn">
        <User :size="12" />
        <span>Author</span>
        <span v-if="selected" class="filter-badge">1</span>
      </button>
    </PopoverTrigger>
    <PopoverContent class="filter-popover-content" align="start">
      <Command>
        <CommandInput placeholder="Filter authors…" />
        <CommandList class="filter-list">
          <div v-if="isLoading && users.length === 0" class="filter-loading">
            <Loader2 :size="14" class="animate-spin" />
          </div>
          <CommandEmpty v-else-if="!isLoading && users.length === 0">No users found.</CommandEmpty>
          <CommandGroup>
            <CommandItem
              v-if="selected"
              value="__clear__"
              @select="() => { emit('select', null); open = false; }"
            >
              <span class="filter-clear-text">Clear selection</span>
            </CommandItem>
            <CommandItem
              v-for="user in users"
              :key="user.login"
              :value="user.login"
              @select="() => handleSelect(user.login)"
            >
              <div class="filter-item">
                <Check :size="13" :style="{ opacity: selected === user.login ? 1 : 0 }" class="filter-check" />
                <img :src="user.avatar_url" alt="" class="user-avatar" />
                <span class="filter-item-text">{{ user.login }}</span>
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

.user-avatar {
  width: 16px;
  height: 16px;
  border-radius: 50%;
  object-fit: cover;
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
