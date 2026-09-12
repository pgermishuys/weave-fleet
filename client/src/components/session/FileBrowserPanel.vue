<script setup lang="ts">
import { computed, inject, provide, ref } from 'vue'
import { FileText, FolderTree, GitCompare, RotateCw, Loader2, Search, X } from 'lucide-vue-next'
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

// Changes tab: a flat list of changed files with their line counts.
const changedFiles = computed(() =>
  [...diffsComposable.diffs.value]
    .sort((a, b) => a.file.localeCompare(b.file))
    .map((d) => {
      const slash = d.file.lastIndexOf('/')
      return {
        file: d.file,
        name: slash >= 0 ? d.file.slice(slash + 1) : d.file,
        dir: slash >= 0 ? d.file.slice(0, slash) : '',
        additions: d.additions,
        deletions: d.deletions,
        status: d.status,
      }
    }),
)

async function handleChangeClick(path: string) {
  contentPanel.selectFile(path)
  contentPanel.setViewMode('diff')
  await selectFile(path)
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
      <div class="file-browser-panel__filter" role="tablist" aria-label="Right panel view">
        <button
          type="button"
          class="file-browser-panel__filter-option"
          :class="{ 'file-browser-panel__filter-option--active': isDiffFilterActive }"
          role="tab"
          :aria-selected="isDiffFilterActive"
          @click="setAllChangedFilter('changed')"
        >
          <GitCompare :size="14" aria-hidden="true" />
          Changes
          <span class="file-browser-panel__count">{{ changedCount }}</span>
        </button>
        <button
          type="button"
          class="file-browser-panel__filter-option"
          :class="{ 'file-browser-panel__filter-option--active': !isDiffFilterActive }"
          role="tab"
          :aria-selected="!isDiffFilterActive"
          @click="setAllChangedFilter('all')"
        >
          <FolderTree :size="14" aria-hidden="true" />
          Files
        </button>
      </div>
      <span class="file-browser-panel__spacer" />
      <button
        class="file-browser-panel__icon-btn"
        :disabled="rootLoading"
        aria-label="Refresh"
        title="Refresh"
        @click="handleRefresh"
      >
        <RotateCw :size="14" :class="{ 'file-browser-panel__refresh-icon--spinning': rootLoading }" />
      </button>
      <slot name="header-actions" />
    </div>

    <slot name="below-header" />

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

    <!-- Changes view: flat list of changed files -->
    <div v-else-if="isDiffFilterActive" class="file-browser-panel__content">
      <p v-if="changedFiles.length === 0" class="file-browser-panel__changes-empty">
        No changes in this session yet.
      </p>
      <div v-else class="file-browser-panel__changes">
        <button
          v-for="change in changedFiles"
          :key="change.file"
          type="button"
          class="file-browser-panel__change"
          :class="{ 'file-browser-panel__change--selected': contentPanel.filesContext.value.selectedFilePath === change.file }"
          :title="change.file"
          :data-status="change.status"
          @click="handleChangeClick(change.file)"
        >
          <FileText :size="14" class="file-browser-panel__change-icon" aria-hidden="true" />
          <span class="file-browser-panel__change-name">{{ change.name }}</span>
          <span class="file-browser-panel__change-dir">{{ change.dir }}</span>
          <span class="file-browser-panel__change-stats">
            <span v-if="change.additions > 0" class="file-browser-panel__change-adds">+{{ change.additions }}</span>
            <span v-if="change.deletions > 0" class="file-browser-panel__change-dels">−{{ change.deletions }}</span>
          </span>
        </button>
      </div>
    </div>

    <!-- Tree view (Files tab) -->
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
  gap: 10px;
  padding: 0;
}

/* Tab strip: the panel's views as tabs, actions on the right. */
.file-browser-panel__header {
  display: flex;
  align-items: center;
  gap: 2px;
  min-height: 56px;
  padding: 0 8px 0 10px;
  border-bottom: 1px solid var(--border);
}

.file-browser-panel__spacer {
  flex: 1;
}

.file-browser-panel__icon-btn {
  display: flex;
  align-items: center;
  justify-content: center;
  width: 28px;
  height: 28px;
  padding: 0;
  background: transparent;
  border: 1px solid transparent;
  border-radius: var(--radius-btn);
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
  gap: 2px;
}

.file-browser-panel__filter-option {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  height: 28px;
  padding: 0 9px;
  background: transparent;
  border: none;
  border-radius: var(--radius-btn);
  cursor: pointer;
  font-size: 13px;
  color: var(--muted);
  transition: background-color var(--transition), color var(--transition);
}

.file-browser-panel__filter-option--active {
  background-color: color-mix(in srgb, var(--text) 9%, transparent);
  color: var(--text);
  font-weight: 500;
}

.file-browser-panel__filter-option:hover:not(.file-browser-panel__filter-option--active) {
  background-color: color-mix(in srgb, var(--text) 5%, transparent);
  color: var(--text);
}

.file-browser-panel__changes {
  display: flex;
  flex-direction: column;
  padding: 0 8px 8px;
}

.file-browser-panel__changes-empty {
  margin: 0;
  padding: 12px 18px;
  font-size: 13px;
  color: var(--muted);
}

.file-browser-panel__change {
  display: flex;
  align-items: center;
  gap: 8px;
  min-height: 30px;
  padding: 0 8px;
  border: 0;
  border-radius: var(--radius-btn);
  background: transparent;
  color: color-mix(in srgb, var(--text) 86%, transparent);
  font-size: 13px;
  text-align: left;
  cursor: pointer;
  transition: background var(--transition), color var(--transition);
}

.file-browser-panel__change:hover {
  background: color-mix(in srgb, var(--text) 5%, transparent);
  color: var(--text);
}

.file-browser-panel__change--selected {
  background: color-mix(in srgb, var(--text) 9%, transparent);
  color: var(--text);
}

.file-browser-panel__change[data-status="deleted"] .file-browser-panel__change-name {
  text-decoration: line-through;
  text-decoration-color: color-mix(in srgb, var(--muted) 60%, transparent);
}

.file-browser-panel__change-icon {
  flex-shrink: 0;
  color: color-mix(in srgb, var(--muted) 75%, transparent);
}

.file-browser-panel__change-name {
  flex-shrink: 0;
  white-space: nowrap;
}

.file-browser-panel__change-dir {
  flex: 1;
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  font-size: 12px;
  color: color-mix(in srgb, var(--muted) 80%, transparent);
}

.file-browser-panel__change-stats {
  display: inline-flex;
  flex-shrink: 0;
  gap: 6px;
  font-family: var(--font-mono-stack);
  font-size: 11.5px;
  font-variant-numeric: tabular-nums;
}

.file-browser-panel__change-adds {
  color: var(--running);
}

.file-browser-panel__change-dels {
  color: var(--error);
}

.file-browser-panel__count {
  font-size: 12px;
  font-weight: 500;
  color: var(--muted);
  font-variant-numeric: tabular-nums;
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
  height: 30px;
  padding: 0 10px;
  margin: 0 10px;
  background-color: color-mix(in srgb, var(--text) 5%, transparent);
  border: 1px solid transparent;
  border-radius: var(--radius-btn);
  transition: border-color var(--transition), background-color var(--transition);
}

.file-browser-panel__search:focus-within {
  background-color: transparent;
  border-color: color-mix(in srgb, var(--accent) 55%, transparent);
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
  font-size: 13px;
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
