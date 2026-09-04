<script setup lang="ts">
import { computed, inject, provide, ref } from 'vue'
import { RotateCw, Loader2, Search, X } from 'lucide-vue-next'
import { useFileBrowser } from '@/composables/use-file-browser'
import { useFindFiles } from '@/composables/use-find-files'
import type { UseDiffsResult } from '@/composables/use-diffs'
import { useContentPanelContext } from '@/composables/use-content-panel'
import FileBrowserTreeNode from './FileBrowserTreeNode.vue'

const SYNTHETIC_PATH_PREFIX = '__visual__/'

interface Props {
  sessionId: string
}

const props = defineProps<Props>()

const sessionIdRef = computed(() => props.sessionId)
const fileBrowser = useFileBrowser(sessionIdRef)
const diffsComposable = inject<UseDiffsResult>('sharedDiffs')!
const contentPanel = useContentPanelContext()

// Provide the composable to child components
provide('fileBrowser', fileBrowser)
provide('diffs', diffsComposable)

const { rootEntries, rootLoading, error, refresh, selectFile } = fileBrowser

// Search functionality
const searchQuery = ref('')
const isSearching = computed(() => searchQuery.value.trim().length >= 2)
const findFiles = useFindFiles(sessionIdRef, searchQuery)

async function handleRefresh() {
  await refresh()
}

function clearSearch() {
  searchQuery.value = ''
}

function handleSearchKeydown(event: KeyboardEvent) {
  if (event.key === 'Escape') {
    clearSearch()
    ;(event.target as HTMLInputElement)?.blur()
  } else if (event.key === 'Enter' && findFiles.files.value.length > 0) {
    handleResultClick(findFiles.files.value[0])
  }
}

function handleResultClick(path: string) {
  // Defensive: search results are real filesystem paths, but guard against
  // synthetic visual-artifact paths ever slipping through so we never fetch them.
  contentPanel.selectFile(path)
  if (path.startsWith(SYNTHETIC_PATH_PREFIX)) return
  selectFile(path)
}

// Diff filter — restrict tree to changed files + their ancestor directories.
const isDiffFilterActive = computed(
  () => contentPanel.filesContext.value.allChangedFilter === 'changed',
)
const changedCount = computed(() => diffsComposable.diffs.value.length)

function setAllChangedFilter(filter: 'all' | 'changed') {
  contentPanel.updateFilesContext({ allChangedFilter: filter })
}

const filteredRootEntries = computed(() => {
  if (!isDiffFilterActive.value) {
    return rootEntries.value
  }

  const changedPaths = new Set(diffsComposable.diffs.value.map(d => d.file))

  const neededDirs = new Set<string>()
  for (const path of changedPaths) {
    let current = path
    while (current.includes('/')) {
      const parent = current.substring(0, current.lastIndexOf('/'))
      if (!parent) break
      neededDirs.add(parent)
      current = parent
    }
  }

  return rootEntries.value.filter(entry =>
    entry.isDirectory
      ? neededDirs.has(entry.relativePath)
      : changedPaths.has(entry.relativePath),
  )
})
</script>

<template>
  <div class="file-browser-panel">
    <div class="file-browser-panel__header">
      <span class="file-browser-panel__title">Files</span>
      <div class="file-browser-panel__filter" role="radiogroup" aria-label="File filter">
        <button
          type="button"
          class="file-browser-panel__filter-option"
          :class="{ 'file-browser-panel__filter-option--active': !isDiffFilterActive }"
          role="radio"
          :aria-checked="!isDiffFilterActive"
          @click="setAllChangedFilter('all')"
        >
          All
        </button>
        <button
          type="button"
          class="file-browser-panel__filter-option"
          :class="{ 'file-browser-panel__filter-option--active': isDiffFilterActive }"
          role="radio"
          :aria-checked="isDiffFilterActive"
          @click="setAllChangedFilter('changed')"
        >
          Changed ({{ changedCount }})
        </button>
      </div>
      <button
        class="file-browser-panel__icon-btn"
        :disabled="rootLoading"
        aria-label="Refresh"
        title="Refresh"
        @click="handleRefresh"
      >
        <RotateCw :size="14" :class="{ 'file-browser-panel__refresh-icon--spinning': rootLoading }" />
      </button>
    </div>

    <!-- Search input -->
    <div class="file-browser-panel__search">
      <Search :size="14" class="file-browser-panel__search-icon" />
      <input
        v-model="searchQuery"
        type="text"
        class="file-browser-panel__search-input"
        placeholder="Search files..."
        @keydown="handleSearchKeydown"
      />
      <button
        v-if="searchQuery"
        class="file-browser-panel__search-clear"
        @click="clearSearch"
        title="Clear search"
      >
        <X :size="14" />
      </button>
    </div>

    <!-- Search results view -->
    <div v-if="isSearching" class="file-browser-panel__content">
      <!-- Search loading state -->
      <div v-if="findFiles.isLoading.value" class="file-browser-panel__loading">
        <Loader2 class="file-browser-panel__spinner" :size="20" />
        <span class="file-browser-panel__loading-text">Searching...</span>
      </div>

      <!-- Search error state -->
      <div v-else-if="findFiles.error.value" class="file-browser-panel__error">
        <p class="file-browser-panel__error-text">{{ findFiles.error.value }}</p>
      </div>

      <!-- Search empty state -->
      <div v-else-if="findFiles.files.value.length === 0" class="file-browser-panel__empty">
        <p class="file-browser-panel__empty-text">No files found</p>
        <p class="file-browser-panel__empty-hint">
          Try a different search query
        </p>
      </div>

      <!-- Search results list -->
      <div v-else class="file-browser-panel__results">
        <button
          v-for="file in findFiles.files.value"
          :key="file"
          class="file-browser-panel__result-item"
          @click="handleResultClick(file)"
        >
          {{ file }}
        </button>
      </div>
    </div>

    <!-- Tree view (default) -->
    <div v-else class="file-browser-panel__content">
      <!-- Loading state -->
      <div v-if="rootLoading && rootEntries.length === 0" class="file-browser-panel__loading">
        <Loader2 class="file-browser-panel__spinner" :size="20" />
        <span class="file-browser-panel__loading-text">Loading files...</span>
      </div>

      <!-- Error state -->
      <div v-else-if="error" class="file-browser-panel__error">
        <p class="file-browser-panel__error-text">{{ error }}</p>
      </div>

      <!-- Empty state -->
      <div v-else-if="rootEntries.length === 0" class="file-browser-panel__empty">
        <p class="file-browser-panel__empty-text">No files found</p>
        <p class="file-browser-panel__empty-hint">
          The session directory is empty or not yet initialized.
        </p>
      </div>

      <!-- Tree -->
      <div v-else class="file-browser-panel__tree">
        <FileBrowserTreeNode
          v-for="entry in filteredRootEntries"
          :key="entry.relativePath"
          :entry="entry"
          :depth="0"
          :session-id="sessionId"
        />
      </div>
    </div>
  </div>
</template>

<style scoped>
.file-browser-panel {
  display: flex;
  flex-direction: column;
  gap: 12px;
  padding: 0;
}

.file-browser-panel__header {
  display: flex;
  align-items: center;
  gap: 12px;
  padding: 8px 8px;
  border-bottom: 1px solid var(--border);
}

.file-browser-panel__title {
  font-size: 11px;
  font-weight: 600;
  color: var(--muted);
  text-transform: uppercase;
  letter-spacing: 0.5px;
  flex: 1;
}

.file-browser-panel__icon-btn {
  display: flex;
  align-items: center;
  justify-content: center;
  padding: 4px;
  background: transparent;
  border: 1px solid transparent;
  border-radius: 4px;
  cursor: pointer;
  color: var(--muted);
  transition: background-color var(--transition), color var(--transition), border-color var(--transition);
}

.file-browser-panel__icon-btn:hover:not(:disabled) {
  background-color: color-mix(in srgb, var(--text) 6%, transparent);
  color: var(--text);
}

.file-browser-panel__icon-btn--active {
  background-color: color-mix(in srgb, var(--accent) 15%, transparent);
  border-color: color-mix(in srgb, var(--accent) 40%, transparent);
  color: var(--accent);
}

.file-browser-panel__icon-btn--active:hover {
  background-color: color-mix(in srgb, var(--accent) 20%, transparent);
  color: var(--accent);
}

.file-browser-panel__icon-btn:disabled {
  cursor: not-allowed;
  opacity: 0.5;
}

.file-browser-panel__filter {
  display: flex;
  align-items: center;
  border: 1px solid var(--border);
  border-radius: 4px;
  overflow: hidden;
}

.file-browser-panel__filter-option {
  padding: 4px 8px;
  background: transparent;
  border: none;
  cursor: pointer;
  font-size: 11px;
  color: var(--muted);
  transition: background-color var(--transition), color var(--transition);
}

.file-browser-panel__filter-option:not(:last-child) {
  border-right: 1px solid var(--border);
}

.file-browser-panel__filter-option--active {
  background-color: color-mix(in srgb, var(--accent) 15%, transparent);
  color: var(--accent);
}

.file-browser-panel__filter-option:hover:not(.file-browser-panel__filter-option--active) {
  background-color: color-mix(in srgb, var(--text) 6%, transparent);
  color: var(--text);
}

.file-browser-panel__refresh-icon--spinning {
  animation: spin 1s linear infinite;
}

@keyframes spin {
  from {
    transform: rotate(0deg);
  }
  to {
    transform: rotate(360deg);
  }
}

/* Search input */
.file-browser-panel__search {
  display: flex;
  align-items: center;
  gap: 6px;
  padding: 6px 8px;
  margin: 0 8px;
  background-color: var(--bg);
  border: 1px solid var(--border);
  border-radius: 4px;
  transition: border-color var(--transition);
}

.file-browser-panel__search:focus-within {
  border-color: var(--primary);
}

.file-browser-panel__search-icon {
  color: var(--muted);
  flex-shrink: 0;
}

.file-browser-panel__search-input {
  flex: 1;
  background: transparent;
  border: none;
  outline: none;
  font-size: 12px;
  color: var(--text);
  padding: 0;
}

.file-browser-panel__search-input::placeholder {
  color: var(--muted);
}

.file-browser-panel__search-clear {
  display: flex;
  align-items: center;
  justify-content: center;
  padding: 2px;
  background: transparent;
  border: none;
  border-radius: 2px;
  cursor: pointer;
  color: var(--muted);
  transition: background-color var(--transition), color var(--transition);
  flex-shrink: 0;
}

.file-browser-panel__search-clear:hover {
  background-color: var(--border);
  color: var(--text);
}

/* Content area */
.file-browser-panel__content {
  display: flex;
  flex-direction: column;
}

.file-browser-panel__loading {
  display: flex;
  flex-direction: column;
  align-items: center;
  gap: 8px;
  padding: 24px 16px;
}

.file-browser-panel__spinner {
  color: var(--muted);
  animation: spin 1s linear infinite;
}

.file-browser-panel__loading-text {
  font-size: 12px;
  color: var(--muted);
}

.file-browser-panel__error {
  padding: 16px;
  background-color: rgba(239, 68, 68, 0.1);
  border-radius: 4px;
  margin: 0 8px;
}

.file-browser-panel__error-text {
  margin: 0;
  font-size: 13px;
  color: #ef4444;
}

.file-browser-panel__empty {
  display: flex;
  flex-direction: column;
  gap: 6px;
  padding: 24px 16px;
  text-align: center;
}

.file-browser-panel__empty-text {
  margin: 0;
  font-size: 13px;
  font-weight: 600;
  color: var(--text);
}

.file-browser-panel__empty-hint {
  margin: 0;
  font-size: 11px;
  line-height: 1.4;
  color: var(--muted);
}

.file-browser-panel__tree {
  display: flex;
  flex-direction: column;
}

/* Search results */
.file-browser-panel__results {
  display: flex;
  flex-direction: column;
}

.file-browser-panel__result-item {
  display: block;
  width: 100%;
  padding: 8px 12px;
  text-align: left;
  background: transparent;
  border: none;
  border-bottom: 1px solid var(--border);
  cursor: pointer;
  font-size: 12px;
  color: var(--text);
  transition: background-color var(--transition);
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}

.file-browser-panel__result-item:hover {
  background-color: var(--bg);
}

.file-browser-panel__result-item:last-child {
  border-bottom: none;
}
</style>
