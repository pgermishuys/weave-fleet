<script setup lang="ts">
import { computed, inject, nextTick, onActivated, onDeactivated, onMounted, provide, ref, watch } from 'vue'
import { Loader2, RotateCw, Search, X } from 'lucide-vue-next'
import { useFileBrowser } from '@/composables/use-file-browser'
import { useFindFiles } from '@/composables/use-find-files'
import type { UseDiffsResult } from '@/composables/use-diffs'
import { useContentPanelContext } from '@/composables/use-content-panel'
import { FIND_TEXT_MIN_CHARS, useFindText } from '@/composables/use-find-text'
import { useSidebarMobile } from '@/composables/use-sidebar-mobile'
import { visibleHits } from '@/lib/find-text'
import { hasRenderedView, useCanvasesStore } from '@/stores/canvases'
import { useFindInFilesStore } from '@/stores/find-in-files'
import { useGoToFileStore } from '@/stores/go-to-file'
import FileBrowserTreeNode from './FileBrowserTreeNode.vue'
import FindInFilesResults from './FindInFilesResults.vue'

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
const findFiles = useFindFiles(sessionIdRef, () => (isSearching.value ? searchQuery.value : null))
// The search also finds folders (they end in "/"); only files open here.
const foundFiles = computed(() => findFiles.files.value.filter((path) => !path.endsWith('/')))

// Find in files: "File names" is the default; "File contents" searches inside the files on the server.
const mode = ref<'names' | 'contents'>('names')
const regex = ref(false)
const matchCase = ref(false)
const searchInput = ref<HTMLInputElement | null>(null)
const expanded = ref<ReadonlySet<string>>(new Set())
const activeKey = ref<string | null>(null)
const inContents = computed(() => mode.value === 'contents')
const findText = useFindText(sessionIdRef, () => (inContents.value ? searchQuery.value : ''), { regex, matchCase })
const hits = computed(() => visibleHits(findText.result.value?.files ?? [], expanded.value))
const canvases = useCanvasesStore()
const goToFile = useGoToFileStore()
const findInFiles = useFindInFilesStore()
const { showRightPanel } = useSidebarMobile()

// A new answer starts over: every file folded, the first line chosen.
watch(findText.result, (answer) => {
  expanded.value = new Set()
  const first = answer?.files[0]?.matches[0]
  activeKey.value = first ? `${answer!.files[0].path}:${first.line}` : null
}, { immediate: true })

function expand(path: string) {
  expanded.value = new Set(expanded.value).add(path)
}

/** A line picked in the results opens its file in a preview tab beside the conversation, at that line. */
function openHit(path: string, line: number) {
  goToFile.focusOnOpen = { sessionId: props.sessionId, path, line }
  // The line is in the source, so Markdown and HTML show their source rather than the rendered page.
  canvases.openFile(props.sessionId, path, { view: hasRenderedView(path) ? 'edit' : undefined })
  showRightPanel()
}

function moveActive(step: number) {
  if (hits.value.length === 0) return
  const at = hits.value.findIndex((hit) => `${hit.path}:${hit.line}` === activeKey.value)
  const next = hits.value[Math.min(Math.max(at + step, 0), hits.value.length - 1)]
  activeKey.value = `${next.path}:${next.line}`
  void nextTick(() => {
    const row = Array.from(document.querySelectorAll<HTMLElement>('[data-hit]')).find((el) => el.dataset.hit === activeKey.value)
    row?.scrollIntoView?.({ block: 'nearest' })
  })
}

// Ctrl Shift F asks for this tab to search inside files; it answers once it is on screen.
let onScreen = true
function takeFindRequest() {
  if (!onScreen || !findInFiles.take(props.sessionId)) return
  mode.value = 'contents'
  void nextTick(() => {
    searchInput.value?.focus()
    searchInput.value?.select()
  })
}
watch(() => findInFiles.request, takeFindRequest)
onMounted(takeFindRequest)
onActivated(() => {
  onScreen = true
  takeFindRequest()
})
onDeactivated(() => {
  onScreen = false
})

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
  } else if (inContents.value) {
    if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
      event.preventDefault()
      moveActive(event.key === 'ArrowDown' ? 1 : -1)
    } else if (event.key === 'Enter') {
      const hit = hits.value.find((candidate) => `${candidate.path}:${candidate.line}` === activeKey.value) ?? hits.value[0]
      if (hit) openHit(hit.path, hit.line)
    }
  } else if (event.key === 'Enter' && foundFiles.value.length > 0) {
    handleResultClick(foundFiles.value[0], true)
  }
}

function handleResultClick(path: string, keep = false) {
  contentPanel.selectFile(path)
  selectFile(path, { keep })
}
</script>

<template>
  <div class="file-browser-panel">
    <div class="file-browser-panel__toolbar">
      <div class="file-browser-panel__search">
        <Search :size="14" class="file-browser-panel__search-icon" />
        <input
          v-model="searchQuery"
          ref="searchInput"
          type="text"
          class="file-browser-panel__search-input"
          :placeholder="inContents ? 'Search inside files…' : 'Search files...'"
          :aria-label="inContents ? 'Search inside files' : 'Search files'"
          @keydown="handleSearchKeydown"
        />
        <template v-if="inContents">
          <button
            type="button"
            class="file-browser-panel__toggle"
            :aria-pressed="matchCase"
            title="Match case"
            aria-label="Match case"
            @click="matchCase = !matchCase"
          >
            Aa
          </button>
          <button
            type="button"
            class="file-browser-panel__toggle"
            :aria-pressed="regex"
            title="Regular expression"
            aria-label="Regular expression"
            @click="regex = !regex"
          >
            .*
          </button>
        </template>
        <button
          v-if="searchQuery"
          class="file-browser-panel__search-clear"
          title="Clear search"
          aria-label="Clear search"
          @click="clearSearch"
        >
          <X :size="14" />
        </button>
      </div>
      <button
        class="file-browser-panel__icon-btn"
        :disabled="rootLoading"
        aria-label="Refresh files"
        title="Refresh files"
        @click="handleRefresh"
      >
        <RotateCw :size="14" :class="{ 'file-browser-panel__refresh-icon--spinning': rootLoading }" />
      </button>
    </div>

    <div class="file-browser-panel__modes" role="group" aria-label="Search in">
      <button type="button" :aria-pressed="!inContents" @click="mode = 'names'">File names</button>
      <button type="button" :aria-pressed="inContents" @click="mode = 'contents'">File contents</button>
    </div>

    <!-- Find in files: matching lines, grouped by file -->
    <div v-if="inContents && findText.isActive.value" class="file-browser-panel__content">
      <FindInFilesResults
        :query="searchQuery"
        :regex="regex"
        :match-case="matchCase"
        :result="findText.result.value"
        :failure="findText.failure.value"
        :is-loading="findText.isLoading.value"
        :took-ms="findText.tookMs.value"
        :expanded="expanded"
        :active="activeKey"
        @open="openHit"
        @expand="expand"
      />
    </div>

    <!-- Search results view -->
    <div v-else-if="!inContents && isSearching" class="file-browser-panel__content">
      <div v-if="findFiles.isLoading.value" class="file-browser-panel__loading">
        <Loader2 class="file-browser-panel__spinner" :size="20" />
        <span class="file-browser-panel__loading-text">Searching...</span>
      </div>

      <div v-else-if="findFiles.error.value" class="file-browser-panel__error">
        <p class="file-browser-panel__error-text">{{ findFiles.error.value }}</p>
      </div>

      <div v-else-if="foundFiles.length === 0" class="file-browser-panel__empty">
        <p class="file-browser-panel__empty-text">No files found</p>
        <p class="file-browser-panel__empty-hint">
          Try a different search query
        </p>
      </div>

      <div v-else class="file-browser-panel__results">
        <button
          v-for="file in foundFiles"
          :key="file"
          class="file-browser-panel__result-item"
          @click="handleResultClick(file)"
        >
          {{ file }}
        </button>
      </div>
    </div>

    <!-- Tree view -->
    <div v-else class="file-browser-panel__content">
      <div v-if="rootLoading && rootEntries.length === 0" class="file-browser-panel__loading">
        <Loader2 class="file-browser-panel__spinner" :size="20" />
        <span class="file-browser-panel__loading-text">Loading files...</span>
      </div>

      <div v-else-if="error" class="file-browser-panel__error">
        <p class="file-browser-panel__error-text">{{ error }}</p>
      </div>

      <div v-else-if="rootEntries.length === 0" class="file-browser-panel__empty">
        <p class="file-browser-panel__empty-text">No files found</p>
        <p class="file-browser-panel__empty-hint">
          The session directory is empty or not yet initialized.
        </p>
      </div>

      <div v-else class="file-browser-panel__tree">
        <FileBrowserTreeNode
          v-for="entry in rootEntries"
          :key="entry.relativePath"
          :entry="entry"
          :depth="0"
          :session-id="sessionId"
        />
      </div>

      <p v-if="inContents" class="file-browser-panel__find-hint">
        {{ searchQuery.trim().length >= FIND_TEXT_MIN_CHARS ? '' : 'Type to search inside every file in this folder.' }}
        <span>Ignored files and build folders are left out.</span>
      </p>
    </div>
  </div>
</template>

<style scoped>
.file-browser-panel__toolbar {
  display: flex;
  align-items: center;
  gap: 4px;
  padding: 0 8px 0 10px;
}

.file-browser-panel {
  display: flex;
  flex-direction: column;
  gap: 6px;
  padding: 8px 0 0;
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

.file-browser-panel__icon-btn:disabled {
  cursor: not-allowed;
  opacity: 0.5;
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
  flex: 1;
  min-width: 0;
  padding: 0 10px;
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
  min-width: 0;
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

/* Find in files */
.file-browser-panel__toggle {
  flex-shrink: 0;
  height: 22px;
  min-width: 24px;
  padding: 0 5px;
  font: 600 11px var(--font-mono-stack);
  color: var(--muted);
  background: transparent;
  border: 1px solid transparent;
  border-radius: 5px;
  cursor: pointer;
}

.file-browser-panel__toggle:hover {
  color: var(--text);
}

.file-browser-panel__toggle[aria-pressed='true'] {
  color: var(--text);
  background: var(--accent-dim);
  border-color: color-mix(in srgb, var(--accent) 45%, transparent);
}

.file-browser-panel__modes {
  display: flex;
  gap: 2px;
  margin: 0 10px;
  padding: 2px;
  background: color-mix(in srgb, var(--text) 5%, transparent);
  border-radius: var(--radius-btn);
}

.file-browser-panel__modes button {
  flex: 1;
  padding: 3px 8px;
  font-size: 12px;
  color: var(--muted);
  background: transparent;
  border: none;
  border-radius: 6px;
  cursor: pointer;
}

.file-browser-panel__modes button[aria-pressed='true'] {
  color: var(--text);
  background: var(--card-bg, var(--panel-bg));
  box-shadow: 0 0 0 1px var(--border);
}

.file-browser-panel__find-hint {
  margin: 0;
  padding: 18px 16px;
  font-size: 12.5px;
  line-height: 1.5;
  text-align: center;
  color: var(--muted);
}

.file-browser-panel__find-hint span {
  display: block;
  font-size: 11.5px;
}

@media (max-width: 716px) {
  .file-browser-panel__search {
    height: 38px;
  }

  .file-browser-panel__toggle {
    height: 28px;
    min-width: 30px;
  }

  .file-browser-panel__modes button {
    padding: 7px 8px;
  }
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
