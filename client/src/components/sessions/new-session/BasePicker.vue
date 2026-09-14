<script setup lang="ts">
import { computed, nextTick, shallowRef, useId, watch } from "vue";
import { Check, ChevronDown, Cloud, GitBranch, Search } from "lucide-vue-next";
import type { BranchInfo } from "@/api/client";
import { Popover, PopoverContent, PopoverTrigger } from "@/components/ui/popover";
import { Switch } from "@/components/ui/switch";

/** Where a new worktree starts: a branch (origin's or local), whether to fetch it first, and the new branch's name. */

const props = defineProps<{
  branches: readonly BranchInfo[];
  /** Where a new worktree starts when nothing is chosen (`origin/main`); null means the current HEAD. */
  defaultBase: string | null;
  currentBranch: string | null;
  isLoading: boolean;
  /** The name the new branch gets from the message, shown as the name field's placeholder. */
  generatedBranch: string | undefined;
  disabled?: boolean;
}>();

const emit = defineEmits<{
  closeAutoFocus: [event: Event];
}>();

const open = defineModel<boolean>("open", { default: false });
/** The chosen base; null for the default. */
const baseBranch = defineModel<string | null>("baseBranch", { required: true });
const fetchOrigin = defineModel<boolean>("fetchOrigin", { required: true });
const branchName = defineModel<string>("branchName", { required: true });

const listId = useId();
const fetchId = useId();
const query = shallowRef("");
const highlightedIndex = shallowRef(0);

/** The ref the worktree will start from, or null while that isn't known yet. */
const effectiveBase = computed(() => {
  if (baseBranch.value) return baseBranch.value;
  if (props.isLoading) return null;
  return props.defaultBase ?? props.currentBranch ?? "HEAD";
});

const isOriginBase = computed(() => effectiveBase.value?.startsWith("origin/") ?? false);

/** The default first, then the rest as the server sends them (most recent commit first). */
const options = computed(() => {
  const needle = query.value.trim().toLowerCase();
  const matching = props.branches.filter((branch) => !needle || branch.name.toLowerCase().includes(needle));
  return [...matching].sort((left, right) => {
    if (left.name === props.defaultBase) return -1;
    if (right.name === props.defaultBase) return 1;
    return 0;
  });
});

function optionDomId(index: number): string {
  return `${listId}-option-${index}`;
}

function choose(name: string): void {
  // The default is sent as "no choice", so the server's default rules apply to it.
  baseBranch.value = name === props.defaultBase ? null : name;
  open.value = false;
}

function handleSearchKeydown(event: KeyboardEvent): void {
  const count = options.value.length;
  if (count === 0) {
    return;
  }

  if (event.key === "ArrowDown") {
    event.preventDefault();
    highlightedIndex.value = (highlightedIndex.value + 1) % count;
    scrollHighlightedIntoView();
  } else if (event.key === "ArrowUp") {
    event.preventDefault();
    highlightedIndex.value = (highlightedIndex.value - 1 + count) % count;
    scrollHighlightedIntoView();
  } else if (event.key === "Enter") {
    event.preventDefault();
    const option = options.value[highlightedIndex.value];
    if (option) {
      choose(option.name);
    }
  }
}

function scrollHighlightedIntoView(): void {
  void nextTick(() => {
    document.getElementById(optionDomId(highlightedIndex.value))?.scrollIntoView?.({ block: "nearest" });
  });
}

watch(query, () => {
  highlightedIndex.value = 0;
});

watch(open, (isOpen) => {
  if (isOpen) {
    query.value = "";
    // After the query watcher, which would put the highlight back on the first row.
    void nextTick(() => {
      const selected = options.value.findIndex((option) => option.name === effectiveBase.value);
      highlightedIndex.value = selected >= 0 ? selected : 0;
      scrollHighlightedIntoView();
    });
  }
});
</script>

<template>
  <Popover v-model:open="open">
    <PopoverTrigger as-child>
      <button
        type="button"
        class="ns-chip"
        data-testid="new-session-base-chip"
        :disabled="disabled"
        :title="effectiveBase ? `New branch starts from ${effectiveBase}` : undefined"
      >
        <GitBranch
          class="ns-chip__icon"
          aria-hidden="true"
        />
        <span class="ns-chip__label">
          <span class="ns-chip__hint">from </span>{{ effectiveBase ?? "the default branch" }}<span
            v-if="isOriginBase && !fetchOrigin"
            class="ns-chip__hint"
          > · no fetch</span>
        </span>
        <ChevronDown
          class="ns-chip__chevron"
          aria-hidden="true"
        />
      </button>
    </PopoverTrigger>

    <PopoverContent
      class="ns-pop"
      side="top"
      align="start"
      :side-offset="6"
      :collision-padding="8"
      @close-auto-focus="emit('closeAutoFocus', $event)"
    >
      <div class="ns-base-search">
        <Search
          class="ns-base-search__icon"
          aria-hidden="true"
        />
        <input
          v-model="query"
          type="text"
          class="ns-base-search__input"
          placeholder="Search branches…"
          autocomplete="off"
          spellcheck="false"
          role="combobox"
          aria-label="Search branches"
          aria-expanded="true"
          :aria-controls="listId"
          :aria-activedescendant="options.length > 0 ? optionDomId(highlightedIndex) : undefined"
          @keydown="handleSearchKeydown"
        >
      </div>

      <div class="ns-pop__label">
        Start from
      </div>
      <div
        :id="listId"
        class="ns-base-list"
        role="listbox"
        aria-label="Branches"
      >
        <button
          v-for="(branch, index) in options"
          :id="optionDomId(index)"
          :key="branch.name"
          type="button"
          class="ns-option"
          role="option"
          tabindex="-1"
          :aria-selected="branch.name === effectiveBase"
          :data-highlighted="index === highlightedIndex ? '' : undefined"
          @click="choose(branch.name)"
          @mousemove="highlightedIndex = index"
        >
          <Cloud
            v-if="branch.isRemote"
            class="ns-option__icon"
            aria-hidden="true"
          />
          <GitBranch
            v-else
            class="ns-option__icon"
            aria-hidden="true"
          />
          <span class="ns-option__text">
            <span class="ns-option__title">
              {{ branch.name }}<span
                v-if="branch.name === defaultBase"
                class="ns-option__tag"
              > · default</span><span
                v-else-if="branch.isCurrent"
                class="ns-option__tag"
              > · checked out</span>
            </span>
            <span
              v-if="branch.message"
              class="ns-option__detail"
            >{{ branch.shortHash }} {{ branch.message }}</span>
          </span>
          <Check
            v-if="branch.name === effectiveBase"
            class="ns-option__check"
            aria-hidden="true"
          />
        </button>
        <p
          v-if="options.length === 0"
          class="ns-pop__note"
        >
          {{ isLoading ? "Loading branches…" : query.trim() ? `No branch matches “${query.trim()}”.` : "No branches found." }}
        </p>
      </div>

      <div class="ns-pop__separator" />
      <div class="ns-base-fetch">
        <label
          :for="fetchId"
          class="ns-base-fetch__text"
        >
          <span class="ns-option__title">Fetch origin first</span>
          <span class="ns-option__detail">{{ isOriginBase ? `Starts from the newest ${effectiveBase}` : "Only for origin’s branches" }}</span>
        </label>
        <Switch
          :id="fetchId"
          v-model="fetchOrigin"
          class="ns-base-fetch__switch"
          data-testid="new-session-base-fetch"
          :disabled="!isOriginBase"
        />
      </div>

      <div class="ns-pop__separator" />
      <div class="ns-field">
        <label
          for="new-session-branch-name"
          class="ns-field__label"
        >New branch</label>
        <input
          id="new-session-branch-name"
          v-model="branchName"
          type="text"
          class="ns-field__input ns-field__input--mono"
          :placeholder="generatedBranch ?? 'Named from your message'"
          autocomplete="off"
          spellcheck="false"
          @keydown.enter.prevent="open = false"
        >
      </div>
    </PopoverContent>
  </Popover>
</template>

<style scoped>
.ns-base-search {
  display: flex;
  align-items: center;
  gap: 7px;
  margin: -1px -1px 2px;
  padding: 5px 8px 7px;
  border-bottom: 1px solid var(--border);
  color: var(--muted);
}

.ns-base-search__icon {
  width: 14px;
  height: 14px;
  flex-shrink: 0;
}

.ns-base-search__input {
  min-width: 0;
  flex: 1;
  border: 0;
  background: transparent;
  color: var(--text);
  font-size: 13px;
  outline: none;
}

.ns-base-search__input::placeholder {
  color: var(--muted);
}

.ns-base-list {
  max-height: min(240px, 36vh);
  overflow-y: auto;
  overscroll-behavior: contain;
}

.ns-base-fetch {
  display: flex;
  align-items: center;
  gap: 10px;
  padding: 6px 8px;
}

/* The switch's shadcn tokens (--input, --background) aren't Fleet's; give it a visible track and thumb. */
.ns-base-fetch__switch[data-state="unchecked"] {
  background: color-mix(in srgb, var(--text) 22%, transparent);
}

.ns-base-fetch :deep([data-slot="switch-thumb"]) {
  background: #fff;
  box-shadow: 0 1px 2px rgba(0, 0, 0, 0.3);
}

.ns-base-fetch__text {
  display: flex;
  min-width: 0;
  flex: 1;
  flex-direction: column;
  cursor: pointer;
}
</style>
