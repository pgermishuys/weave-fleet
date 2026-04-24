<script setup lang="ts">
import type { BoardSource } from "@/lib/board-api";
import { computed, ref, shallowRef, watch } from "vue";
import { createBoardSource, deleteBoardSource, listBoardSources } from "@/lib/board-api";
import { useGitHubBookmarks } from "@/plugins/builtin/github/composables/use-github-bookmarks";

interface Props {
  boardId: string | null;
}

interface ParsedSourceConfig {
  repository: string | null;
  labels: string | null;
}

interface SourceViewModel {
  id: string;
  providerType: string;
  repositoryLabel: string;
  labelsLabel: string | null;
  lastSyncLabel: string;
}

const props = defineProps<Props>();

const dateTimeFormatter = new Intl.DateTimeFormat("en-US", {
  dateStyle: "medium",
  timeStyle: "short",
});

const sources = ref<BoardSource[]>([]);
const isLoadingSources = shallowRef(false);
const isCreatingSource = shallowRef(false);
const pendingDeleteSourceId = shallowRef<string | null>(null);
const sourceError = shallowRef<string | null>(null);
const selectedRepository = shallowRef("");
const labelFilter = shallowRef("");

const {
  bookmarks,
  error: bookmarksError,
  isLoading: isLoadingBookmarks,
  refresh: refreshBookmarks,
} = useGitHubBookmarks();

const hasBoard = computed(() => props.boardId !== null);

const bookmarkedRepoOptions = computed(() => {
  return [...bookmarks.value].sort((left, right) => left.fullName.localeCompare(right.fullName));
});

const sourceViewModels = computed<SourceViewModel[]>(() => {
  return [...sources.value]
    .map((source) => {
      const config = parseSourceConfig(source.config);

      return {
        id: source.id,
        providerType: source.providerType,
        repositoryLabel: config.repository ?? "Unknown repository",
        labelsLabel: config.labels,
        lastSyncLabel: formatLastSync(source.lastSyncAt),
      };
    })
    .sort((left, right) => left.repositoryLabel.localeCompare(right.repositoryLabel));
});

const sourceSignatures = computed(() => {
  return new Set(sourceViewModels.value.map((source) => createSourceSignature(source.repositoryLabel, source.labelsLabel)));
});

const normalizedSelectedLabels = computed(() => normalizeLabels(labelFilter.value));

const isDuplicateSource = computed(() => {
  if (selectedRepository.value.trim().length === 0) {
    return false;
  }

  return sourceSignatures.value.has(createSourceSignature(selectedRepository.value, normalizedSelectedLabels.value));
});

const canSubmit = computed(() => {
  return hasBoard.value
    && selectedRepository.value.trim().length > 0
    && !isLoadingSources.value
    && !isCreatingSource.value
    && pendingDeleteSourceId.value === null
    && !isDuplicateSource.value;
});

watch(
  () => props.boardId,
  async (boardId, _previousBoardId, onCleanup) => {
    let cancelled = false;
    onCleanup(() => {
      cancelled = true;
    });

    sourceError.value = null;

    if (boardId === null) {
      sources.value = [];
      return;
    }

    isLoadingSources.value = true;

    try {
      const nextSources = await listBoardSources(boardId);
      if (!cancelled) {
        sources.value = nextSources;
      }
    } catch (error) {
      if (!cancelled) {
        sourceError.value = toErrorMessage(error, "Failed to load board sources.");
        sources.value = [];
      }
    } finally {
      if (!cancelled) {
        isLoadingSources.value = false;
      }
    }
  },
  { immediate: true },
);

function parseSourceConfig(config: string): ParsedSourceConfig {
  try {
    const payload = JSON.parse(config) as {
      repository?: unknown;
      owner?: unknown;
      repo?: unknown;
      labels?: unknown;
    };
    const repository = typeof payload.repository === "string"
      ? payload.repository.trim()
      : typeof payload.owner === "string" && typeof payload.repo === "string"
        ? `${payload.owner.trim()}/${payload.repo.trim()}`
        : "";

    return {
      repository: repository.length > 0 ? repository : null,
      labels: normalizeLabels(payload.labels),
    };
  } catch {
    return {
      repository: null,
      labels: null,
    };
  }
}

function normalizeLabels(value: unknown): string | null {
  if (Array.isArray(value)) {
    const labels = value
      .filter((label): label is string => typeof label === "string")
      .map((label) => label.trim())
      .filter((label) => label.length > 0);

    return labels.length > 0 ? labels.join(", ") : null;
  }

  if (typeof value !== "string") {
    return null;
  }

  const labels = value
    .split(",")
    .map((label) => label.trim())
    .filter((label) => label.length > 0);

  return labels.length > 0 ? labels.join(", ") : null;
}

function createSourceSignature(repository: string, labels: string | null): string {
  return `${repository.trim().toLowerCase()}::${labels?.toLowerCase() ?? ""}`;
}

function formatLastSync(value: string | null): string {
  if (!value) {
    return "Not synced yet";
  }

  const parsed = new Date(value);
  if (Number.isNaN(parsed.getTime())) {
    return "Unknown sync time";
  }

  return dateTimeFormatter.format(parsed);
}

function toErrorMessage(error: unknown, fallback: string): string {
  if (error instanceof Error && error.message.trim().length > 0) {
    return error.message;
  }

  return fallback;
}

async function handleAddSource(): Promise<void> {
  if (!props.boardId || !canSubmit.value) {
    return;
  }

  isCreatingSource.value = true;
  sourceError.value = null;

  try {
    const config = {
      repository: selectedRepository.value.trim(),
      ...(normalizedSelectedLabels.value ? { labels: normalizedSelectedLabels.value } : {}),
    };

    const createdSource = await createBoardSource(props.boardId, {
      providerType: "github",
      config: JSON.stringify(config),
    });

    sources.value = [...sources.value, createdSource];
    labelFilter.value = "";
  } catch (error) {
    sourceError.value = toErrorMessage(error, "Failed to create board source.");
  } finally {
    isCreatingSource.value = false;
  }
}

async function handleRemoveSource(sourceId: string): Promise<void> {
  if (!props.boardId) {
    return;
  }

  pendingDeleteSourceId.value = sourceId;
  sourceError.value = null;

  try {
    await deleteBoardSource(props.boardId, sourceId);
    sources.value = sources.value.filter((source) => source.id !== sourceId);
  } catch (error) {
    sourceError.value = toErrorMessage(error, "Failed to remove board source.");
  } finally {
    pendingDeleteSourceId.value = null;
  }
}
</script>

<template>
  <section
    class="board-source-config"
    aria-label="Board source configuration"
  >
    <div class="board-source-config__header">
      <div>
        <p class="board-source-config__eyebrow">
          Sources
        </p>
        <h2 class="board-source-config__title">
          Sync board cards from bookmarked repositories
        </h2>
        <p class="board-source-config__description">
          Add GitHub issue sources from your bookmarks, optionally narrowing sync to matching labels.
        </p>
      </div>
    </div>

    <div
      v-if="sourceError || bookmarksError"
      class="board-source-config__banner"
      role="alert"
    >
      <span>{{ sourceError ?? bookmarksError }}</span>
      <button
        v-if="bookmarksError"
        type="button"
        class="board-source-config__ghost-button"
        @click="refreshBookmarks"
      >
        Retry bookmarks
      </button>
    </div>

    <div
      v-if="!hasBoard"
      class="board-source-config__empty"
      data-testid="board-source-no-board"
    >
      Create a board lane first to enable source syncing.
    </div>

    <template v-else>
      <form
        class="board-source-config__form"
        data-testid="board-source-form"
        @submit.prevent="handleAddSource"
      >
        <label class="board-source-config__field">
          <span class="board-source-config__label">Bookmarked repository</span>
          <select
            v-model="selectedRepository"
            class="board-source-config__select"
            data-testid="board-source-repo-select"
            :disabled="isLoadingBookmarks || isLoadingSources || isCreatingSource || pendingDeleteSourceId !== null"
          >
            <option value="">
              {{ isLoadingBookmarks ? "Loading bookmarks…" : "Select a repository" }}
            </option>
            <option
              v-for="bookmark in bookmarkedRepoOptions"
              :key="bookmark.fullName"
              :value="bookmark.fullName"
            >
              {{ bookmark.fullName }}
            </option>
          </select>
        </label>

        <label class="board-source-config__field">
          <span class="board-source-config__label">Label filter (optional)</span>
          <input
            v-model="labelFilter"
            class="board-source-config__input"
            data-testid="board-source-label-filter"
            type="text"
            placeholder="bug, priority:high"
            :disabled="isLoadingSources || isCreatingSource || pendingDeleteSourceId !== null"
          >
        </label>

        <button
          type="submit"
          class="board-source-config__button"
          data-testid="board-source-submit"
          :disabled="!canSubmit"
        >
          {{ isCreatingSource ? "Adding…" : "Add source" }}
        </button>
      </form>

      <p
        v-if="bookmarkedRepoOptions.length === 0 && !isLoadingBookmarks"
        class="board-source-config__helper"
      >
        Bookmark a repository in the GitHub panel to add it as a board source.
      </p>
      <p
        v-else-if="isDuplicateSource"
        class="board-source-config__helper"
      >
        This repository and label filter is already configured.
      </p>

      <div
        v-if="isLoadingSources"
        class="board-source-config__empty"
      >
        Loading configured sources…
      </div>
      <ul
        v-else-if="sourceViewModels.length > 0"
        class="board-source-config__list"
        data-testid="board-source-list"
      >
        <li
          v-for="source in sourceViewModels"
          :key="source.id"
          class="board-source-config__item"
          data-testid="board-source-item"
        >
          <div class="board-source-config__item-copy">
            <div class="board-source-config__item-heading">
              <span class="board-source-config__provider">{{ source.providerType }}</span>
              <span class="board-source-config__repository">{{ source.repositoryLabel }}</span>
            </div>
            <p class="board-source-config__meta">
              <span v-if="source.labelsLabel">Labels: {{ source.labelsLabel }}</span>
              <span v-else>No label filter</span>
              <span>•</span>
              <span>Last sync: {{ source.lastSyncLabel }}</span>
            </p>
          </div>

          <button
            type="button"
            class="board-source-config__ghost-button"
            data-testid="board-source-remove"
            :disabled="pendingDeleteSourceId === source.id || isCreatingSource"
            @click="handleRemoveSource(source.id)"
          >
            {{ pendingDeleteSourceId === source.id ? "Removing…" : "Remove" }}
          </button>
        </li>
      </ul>
      <div
        v-else
        class="board-source-config__empty"
        data-testid="board-source-empty"
      >
        No configured sources yet.
      </div>
    </template>
  </section>
</template>

<style scoped>
.board-source-config {
  display: flex;
  flex-direction: column;
  gap: 12px;
  padding: 12px 24px 0;
}

.board-source-config__header {
  display: flex;
  align-items: flex-start;
  justify-content: space-between;
  gap: 12px;
}

.board-source-config__eyebrow {
  margin: 0 0 6px;
  font-size: 11px;
  font-weight: 600;
  letter-spacing: 0.05em;
  text-transform: uppercase;
  color: var(--muted);
}

.board-source-config__title {
  margin: 0;
  font-size: 18px;
  font-weight: 700;
  color: var(--text);
}

.board-source-config__description,
.board-source-config__helper,
.board-source-config__meta,
.board-source-config__empty {
  margin: 0;
  font-size: 13px;
  line-height: 1.5;
  color: var(--muted);
}

.board-source-config__form,
.board-source-config__list,
.board-source-config__empty,
.board-source-config__banner {
  padding: 12px;
  border: 1px solid var(--border);
  border-radius: var(--radius-card);
  background: var(--card-bg);
}

.board-source-config__form {
  display: grid;
  grid-template-columns: minmax(0, 1.4fr) minmax(0, 1fr) auto;
  gap: 12px;
  align-items: end;
}

.board-source-config__field {
  display: flex;
  flex-direction: column;
  gap: 6px;
  min-width: 0;
}

.board-source-config__label {
  font-size: 11px;
  font-weight: 600;
  letter-spacing: 0.04em;
  text-transform: uppercase;
  color: var(--muted);
}

.board-source-config__select,
.board-source-config__input {
  width: 100%;
  min-width: 0;
  padding: 10px 12px;
  border: 1px solid var(--border);
  border-radius: var(--radius-btn);
  background: rgba(255, 255, 255, 0.02);
  color: var(--text);
}

.board-source-config__button,
.board-source-config__ghost-button {
  padding: 10px 14px;
  border: 1px solid var(--border);
  border-radius: var(--radius-btn);
  background: var(--card-bg);
  color: var(--text);
  font-size: 13px;
  font-weight: 600;
  cursor: pointer;
}

.board-source-config__button {
  border-color: var(--accent);
  background: var(--accent);
  color: #fff;
}

.board-source-config__button:disabled,
.board-source-config__ghost-button:disabled,
.board-source-config__select:disabled,
.board-source-config__input:disabled {
  opacity: 0.6;
  cursor: not-allowed;
}

.board-source-config__banner {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
  border-color: rgba(248, 113, 113, 0.4);
  background: rgba(127, 29, 29, 0.22);
  color: #fecaca;
}

.board-source-config__list {
  display: flex;
  flex-direction: column;
  gap: 10px;
  list-style: none;
  margin: 0;
}

.board-source-config__item {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
  padding: 12px;
  border: 1px solid var(--border);
  border-radius: 10px;
  background: rgba(255, 255, 255, 0.02);
}

.board-source-config__item-copy {
  display: flex;
  flex-direction: column;
  gap: 4px;
  min-width: 0;
}

.board-source-config__item-heading {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 8px;
}

.board-source-config__provider {
  display: inline-flex;
  align-items: center;
  padding: 2px 8px;
  border-radius: 999px;
  background: rgba(255, 255, 255, 0.06);
  font-size: 11px;
  font-weight: 700;
  letter-spacing: 0.04em;
  text-transform: uppercase;
  color: var(--muted);
}

.board-source-config__repository {
  font-size: 14px;
  font-weight: 600;
  color: var(--text);
}

@media (max-width: 960px) {
  .board-source-config__form {
    grid-template-columns: 1fr;
  }

  .board-source-config__item,
  .board-source-config__banner {
    flex-direction: column;
    align-items: stretch;
  }
}
</style>
