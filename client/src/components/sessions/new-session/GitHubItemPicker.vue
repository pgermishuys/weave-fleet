<script setup lang="ts">
import { computed } from "vue";
import { Github, LoaderCircle } from "lucide-vue-next";
import GitHubItemIcon from "@/components/github/GitHubItemIcon.vue";
import { itemPrFacts, itemResourceId, type GitHubItemSummary } from "@/lib/github-items";
import type { PickerGroup } from "@/composables/use-github-picker";
import { prState } from "@/lib/pr-state";

/**
 * The `#` popup in the new-session message: the folder's open pull requests and issues. Picking one starts the
 * session from it; one that already has a session says so, and Shift+Enter opens that session instead.
 */
const props = defineProps<{
  /** `owner/repo`, or null when the folder has no GitHub remote. */
  repository: string | null;
  query: string;
  groups: PickerGroup[];
  selectedIndex: number;
  isLoading: boolean;
  error: string | null;
  /** `owner/repo#123` keys that already have a Fleet session. */
  withSessions: ReadonlySet<string>;
}>();

const emit = defineEmits<{
  pick: [item: GitHubItemSummary];
  hover: [index: number];
}>();

const rows = computed(() => {
  let index = 0;
  return props.groups.map((group) => ({
    label: group.label,
    items: group.items.map((item) => ({ item, index: index++ })),
  }));
});

function state(item: GitHubItemSummary) {
  if (item.kind === "issue") return item.state === "closed" ? "closed" : "open";
  return prState(itemPrFacts(item));
}
</script>

<template>
  <div
    class="gh-picker"
    role="listbox"
    :aria-label="repository ? `Pull requests and issues in ${repository}` : 'Pull requests and issues'"
    data-testid="github-item-picker"
    @mousedown.prevent
  >
    <div class="gh-picker__head">
      <Github
        :size="13"
        aria-hidden="true"
      />
      <span>{{ repository ?? "GitHub" }}</span>
      <LoaderCircle
        v-if="isLoading"
        :size="12"
        class="gh-picker__spin"
        aria-hidden="true"
      />
      <span class="gh-picker__query">#{{ query }}</span>
    </div>

    <p
      v-if="!repository"
      class="gh-picker__note"
    >
      Pick a folder whose repository is on GitHub to link one of its issues or pull requests.
    </p>
    <p
      v-else-if="error"
      class="gh-picker__note gh-picker__note--error"
    >
      {{ error }}
    </p>
    <p
      v-else-if="!isLoading && groups.length === 0"
      class="gh-picker__note"
    >
      No open pull requests or issues match.
    </p>

    <template
      v-for="group in rows"
      :key="group.label"
    >
      <p class="gh-picker__group">
        {{ group.label }}
      </p>
      <div
        v-for="{ item, index } in group.items"
        :key="itemResourceId(item)"
        class="gh-picker__row"
        :class="{ 'gh-picker__row--selected': index === selectedIndex }"
        role="option"
        :aria-selected="index === selectedIndex"
        data-testid="github-item-picker-row"
        @mouseenter="emit('hover', index)"
        @click="emit('pick', item)"
      >
        <GitHubItemIcon
          :kind="item.kind"
          :state="state(item)"
          :size="14"
        />
        <span class="gh-picker__number">#{{ item.number }}</span>
        <span class="gh-picker__title">{{ item.title }}</span>
        <span
          v-if="withSessions.has(itemResourceId(item).toLowerCase())"
          class="gh-picker__extra"
        >has a session</span>
      </div>
    </template>

    <div class="gh-picker__foot">
      <span><kbd>↑↓</kbd> move</span>
      <span><kbd>Enter</kbd> start from it</span>
      <span><kbd>Shift</kbd>+<kbd>Enter</kbd> open its session</span>
      <span class="gh-picker__esc"><kbd>Esc</kbd></span>
    </div>
  </div>
</template>

<style scoped>
.gh-picker {
  position: absolute;
  right: 0;
  bottom: calc(100% + 8px);
  left: 0;
  z-index: 250;
  max-height: 340px;
  overflow-y: auto;
  overscroll-behavior: contain;
  border: 1px solid var(--border);
  border-radius: var(--radius-card);
  background: var(--card-bg);
  box-shadow: 0 10px 30px rgba(0, 0, 0, 0.3);
  color: var(--text);
}

.gh-picker__head {
  position: sticky;
  top: 0;
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 8px 12px;
  border-bottom: 1px solid var(--border);
  background: var(--card-bg);
  color: var(--muted);
  font-size: 12px;
}

.gh-picker__query {
  margin-left: auto;
  color: var(--text);
  font-family: var(--font-mono-stack);
}

.gh-picker__spin {
  animation: gh-picker-spin 1s linear infinite;
}

@keyframes gh-picker-spin {
  to { transform: rotate(360deg); }
}

.gh-picker__note {
  margin: 0;
  padding: 12px;
  color: var(--muted);
  font-size: 12.5px;
}

.gh-picker__note--error {
  color: var(--error);
}

.gh-picker__group {
  margin: 0;
  padding: 8px 12px 3px;
  color: var(--muted);
  font-size: 10.5px;
  font-weight: 600;
  letter-spacing: 0.05em;
  text-transform: uppercase;
}

.gh-picker__row {
  display: grid;
  grid-template-columns: 16px 44px minmax(0, 1fr) auto;
  gap: 8px;
  align-items: center;
  padding: 6px 12px;
  font-size: 12.5px;
  cursor: pointer;
}

.gh-picker__row--selected {
  background: color-mix(in srgb, var(--accent) 14%, transparent);
}

.gh-picker__number {
  color: var(--muted);
  font-family: var(--font-mono-stack);
  font-size: 11.5px;
  text-align: right;
}

.gh-picker__title {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.gh-picker__extra {
  color: var(--muted);
  font-size: 11px;
}

.gh-picker__foot {
  position: sticky;
  bottom: 0;
  display: flex;
  flex-wrap: wrap;
  gap: 4px 12px;
  padding: 7px 12px;
  border-top: 1px solid var(--border);
  background: var(--card-bg);
  color: var(--muted);
  font-size: 11px;
}

.gh-picker__esc {
  margin-left: auto;
}

.gh-picker__foot kbd {
  padding: 0 4px;
  border-radius: 4px;
  background: color-mix(in srgb, var(--text) 8%, transparent);
  font-family: var(--font-mono-stack);
  font-size: 10.5px;
}

@media (prefers-reduced-motion: reduce) {
  .gh-picker__spin { animation: none; }
}
</style>
