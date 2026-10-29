<script setup lang="ts">
import { computed } from 'vue'
import { Loader2 } from 'lucide-vue-next'
import { fileIcon, fileName } from '@/lib/canvas-registry'
import type { FindTextFailure } from '@/composables/use-find-text'
import { MATCHES_PER_FILE, segmentsOf, type TextSearchResult } from '@/lib/find-text'

const props = defineProps<{
  query: string
  regex: boolean
  matchCase: boolean
  result: TextSearchResult | null
  failure: FindTextFailure | null
  isLoading: boolean
  tookMs: number
  /** Files whose every match is shown, not the first few. */
  expanded: ReadonlySet<string>
  /** The line the arrow keys are on, as "path:line". */
  active: string | null
}>()

const emit = defineEmits<{
  open: [path: string, line: number]
  expand: [path: string]
}>()

const folderOf = (path: string) => (path.includes('/') ? path.slice(0, path.lastIndexOf('/')) : '')
const keyOf = (path: string, line: number) => `${path}:${line}`

const hasMatches = computed(() => (props.result?.files.length ?? 0) > 0)
const seconds = computed(() => `${(props.tookMs / 1000).toFixed(1)} s`)

const summary = computed(() => {
  const found = props.result
  if (!found || found.files.length === 0) return ''
  const files = `${found.files.length} file${found.files.length === 1 ? '' : 's'}`
  const head =
    found.stopped === 'limit'
      ? `First ${found.matches} matches, in ${files}`
      : `${found.matches} match${found.matches === 1 ? '' : 'es'} in ${files}`
  const tail = found.stopped === 'deadline' ? 'stopped after 10 s' : seconds.value
  return `${head} · ${tail}${found.engine === 'walk' ? ' · not a git repository' : ''}`
})

const emptyHint = computed(() => {
  const hint = props.regex ? 'Try turning off the regex switch.' : 'Check the spelling, or try the regex switch.'
  return props.matchCase ? `${hint} Match case is on.` : hint
})

function shown<T>(path: string, matches: T[]): T[] {
  return props.expanded.has(path) ? matches : matches.slice(0, MATCHES_PER_FILE)
}
</script>

<template>
  <div class="find-results">
    <div v-if="failure?.kind === 'pattern'" class="find-results__note find-results__note--error" role="alert">
      <strong>That isn't a valid pattern.</strong>
      <span class="find-results__mono">{{ failure.message }}</span>
    </div>

    <div v-else-if="failure?.kind === 'unsupported'" class="find-results__note find-results__note--warn" role="alert">
      <strong>This machine can't search inside files yet.</strong>
      <span>The Fleet running on it is older. Update it from Settings → Updates & about, then search again.</span>
    </div>

    <div v-else-if="failure" class="find-results__note find-results__note--error" role="alert">
      <strong>The search failed.</strong>
      <span>{{ failure.message }}</span>
    </div>

    <div v-else-if="!result" class="find-results__status">
      <Loader2 v-if="isLoading" :size="16" class="find-results__spinner" />
      <span>Searching…</span>
    </div>

    <template v-else>
      <div v-if="summary" class="find-results__summary" :class="{ 'find-results__summary--busy': isLoading }">
        {{ summary }}
      </div>

      <div v-if="!hasMatches && result.stopped === 'deadline'" class="find-results__note find-results__note--warn">
        <strong>The search ran out of time before it found anything.</strong>
        <span>That isn't “no matches”. Try a longer or more exact search.</span>
      </div>

      <div v-else-if="!hasMatches" class="find-results__empty">
        <p class="find-results__empty-text">No matches for “{{ query.trim() }}”.</p>
        <p class="find-results__empty-hint">{{ emptyHint }}</p>
        <p v-if="result.engine === 'walk'" class="find-results__empty-hint">
          This folder isn't a git repository, so Fleet searched every file except build and dependency folders.
        </p>
      </div>

      <div v-else class="find-results__list" :class="{ 'find-results__list--busy': isLoading }">
        <div v-for="file in result.files" :key="file.path" class="find-results__group">
          <button
            type="button"
            class="find-results__file"
            :title="file.path"
            @click="emit('open', file.path, file.matches[0]?.line ?? 1)"
          >
            <component :is="fileIcon(file.path)" :size="13" class="find-results__file-icon" />
            <span class="find-results__file-name">{{ fileName(file.path) }}</span>
            <span class="find-results__file-dir">{{ folderOf(file.path) }}</span>
            <span class="find-results__count">{{ file.total }}</span>
          </button>
          <button
            v-for="match in shown(file.path, file.matches)"
            :key="keyOf(file.path, match.line)"
            type="button"
            class="find-results__hit"
            :class="{ 'find-results__hit--active': active === keyOf(file.path, match.line) }"
            :data-hit="keyOf(file.path, match.line)"
            @click="emit('open', file.path, match.line)"
          >
            <span class="find-results__line">{{ match.line }}</span>
            <span class="find-results__text"><template v-for="(part, index) in segmentsOf(match.text, match.ranges)" :key="index"><mark v-if="part.marked">{{ part.text }}</mark><template v-else>{{ part.text }}</template></template></span>
          </button>
          <button
            v-if="file.matches.length > MATCHES_PER_FILE && !expanded.has(file.path)"
            type="button"
            class="find-results__more"
            @click="emit('expand', file.path)"
          >
            Show {{ file.matches.length - MATCHES_PER_FILE }} more in this file
          </button>
        </div>
      </div>

      <div v-if="hasMatches && result.stopped === 'limit'" class="find-results__note find-results__note--warn">
        <strong>Showing the first {{ result.matches }} matches.</strong>
        There are more. Search for something longer, or turn on the regex switch.
      </div>
      <div v-else-if="hasMatches && result.stopped === 'deadline'" class="find-results__note find-results__note--warn">
        <strong>Stopped after 10 seconds.</strong> These are the matches found so far.
      </div>
      <div v-if="hasMatches && result.engine === 'walk'" class="find-results__note">
        This folder isn't a git repository, so Fleet searched every file except build and dependency folders.
      </div>
    </template>
  </div>
</template>

<style scoped>
.find-results {
  display: flex;
  flex-direction: column;
  padding: 0 6px 14px;
  min-width: 0;
}

.find-results__status {
  display: flex;
  align-items: center;
  justify-content: center;
  gap: 8px;
  padding: 24px 16px;
  font-size: 12px;
  color: var(--muted);
}

.find-results__spinner {
  animation: find-spin 1s linear infinite;
}

@keyframes find-spin {
  to {
    transform: rotate(360deg);
  }
}

.find-results__summary {
  padding: 2px 6px 6px;
  font-size: 11.5px;
  color: var(--muted);
}

.find-results__summary--busy,
.find-results__list--busy {
  opacity: 0.6;
}

.find-results__empty {
  display: flex;
  flex-direction: column;
  gap: 6px;
  padding: 24px 12px;
  text-align: center;
}

.find-results__empty-text {
  margin: 0;
  font-size: 13px;
  font-weight: 600;
  color: var(--text);
}

.find-results__empty-hint {
  margin: 0;
  font-size: 11.5px;
  line-height: 1.4;
  color: var(--muted);
}

.find-results__note {
  display: flex;
  flex-direction: column;
  gap: 2px;
  margin: 8px 2px;
  padding: 9px 12px;
  font-size: 12.5px;
  line-height: 1.45;
  color: var(--muted);
  background: var(--card-bg, transparent);
  border: 1px solid var(--border);
  border-radius: var(--radius-card, 10px);
}

.find-results__note strong {
  color: var(--text);
  font-weight: 600;
}

.find-results__note--warn {
  border-color: color-mix(in srgb, var(--idle) 45%, transparent);
}

.find-results__note--warn strong {
  color: var(--idle);
}

.find-results__note--error {
  border-color: color-mix(in srgb, var(--error, #ef4444) 45%, transparent);
}

.find-results__note--error strong {
  color: var(--error, #ef4444);
}

.find-results__mono {
  font-family: var(--font-mono-stack);
  font-size: 11.5px;
  overflow-wrap: anywhere;
}

.find-results__group {
  margin: 0 0 2px;
}

.find-results__file {
  display: flex;
  align-items: center;
  gap: 6px;
  width: 100%;
  padding: 5px 6px;
  font-size: 12.5px;
  text-align: left;
  color: var(--text);
  background: transparent;
  border: none;
  border-radius: 6px;
  cursor: pointer;
}

.find-results__file:hover {
  background: color-mix(in srgb, var(--text) 5%, transparent);
}

.find-results__file-icon {
  flex: none;
  color: var(--muted);
}

.find-results__file-name {
  font-weight: 600;
  white-space: nowrap;
}

.find-results__file-dir {
  flex: 1;
  min-width: 0;
  overflow: hidden;
  font-size: 11px;
  color: var(--muted);
  text-overflow: ellipsis;
  white-space: nowrap;
}

.find-results__count {
  flex: none;
  padding: 0 6px;
  font-size: 10.5px;
  color: var(--muted);
  background: color-mix(in srgb, var(--text) 8%, transparent);
  border-radius: 99px;
}

.find-results__hit {
  display: flex;
  gap: 8px;
  width: 100%;
  min-width: 0;
  padding: 2px 6px 2px 24px;
  font: 12px/1.55 var(--font-mono-stack);
  text-align: left;
  color: color-mix(in srgb, var(--text) 82%, transparent);
  background: transparent;
  border: none;
  border-radius: 5px;
  cursor: pointer;
}

.find-results__hit:hover,
.find-results__hit--active {
  background: color-mix(in srgb, var(--text) 7%, transparent);
}

.find-results__hit--active {
  outline: 1px solid color-mix(in srgb, var(--accent) 50%, transparent);
}

.find-results__line {
  flex: none;
  min-width: 3.2ch;
  text-align: right;
  color: var(--muted);
}

.find-results__text {
  min-width: 0;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: pre;
}

.find-results mark {
  padding: 0;
  color: var(--text);
  background: color-mix(in srgb, var(--idle) 30%, transparent);
  border-radius: 2px;
}

.find-results__more {
  margin: 1px 0 4px 24px;
  padding: 2px 6px;
  font-size: 11.5px;
  color: var(--muted);
  background: transparent;
  border: none;
  border-radius: 5px;
  cursor: pointer;
}

.find-results__more:hover {
  color: var(--text);
  background: color-mix(in srgb, var(--text) 6%, transparent);
}

@media (max-width: 716px) {
  .find-results__hit {
    padding-top: 7px;
    padding-bottom: 7px;
    padding-left: 22px;
  }

  .find-results__file {
    padding-top: 9px;
    padding-bottom: 9px;
  }

  .find-results__more {
    padding: 8px 6px;
  }
}
</style>
