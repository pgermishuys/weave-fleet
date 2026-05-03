<script setup lang="ts">
import { ExternalLink, Github, TriangleAlert } from "lucide-vue-next";
import type { BoardCard } from "@/stores/board";
import { computed, shallowRef } from "vue";
import { formatRelativeTime } from "@/lib/format-utils";

interface Props {
  card: BoardCard;
  isDragging: boolean;
  isMutating: boolean;
}

interface Emits {
  rename: [title: string];
  delete: [];
  archive: [];
  dragStart: [];
  dragEnd: [];
}

const props = defineProps<Props>();
const emit = defineEmits<Emits>();

interface GitHubCardMetadata {
  number: number | null;
  state: string | null;
  labels: string[];
  assignee: string | null;
  htmlUrl: string | null;
  updatedAt: string | null;
  stale: boolean;
}

const isEditing = shallowRef(false);
const titleDraft = shallowRef("");

function parseMetadataObject(metadata: string | null): Record<string, unknown> | null {
  const value = metadata?.trim();
  if (!value) {
    return null;
  }

  try {
    const parsed = JSON.parse(value) as unknown;
    return typeof parsed === "object" && parsed !== null && !Array.isArray(parsed)
      ? parsed as Record<string, unknown>
      : null;
  } catch {
    return null;
  }
}

function getMetadataString(metadata: Record<string, unknown>, key: string): string | null {
  const value = metadata[key];
  return typeof value === "string" && value.trim().length > 0 ? value.trim() : null;
}

function getMetadataNumber(metadata: Record<string, unknown>, key: string): number | null {
  const value = metadata[key];
  return typeof value === "number" ? value : null;
}

function getMetadataLabels(metadata: Record<string, unknown>, key: string): string[] {
  const value = metadata[key];
  return Array.isArray(value)
    ? value.filter((item): item is string => typeof item === "string" && item.trim().length > 0)
    : [];
}

function getMetadataBoolean(metadata: Record<string, unknown>, key: string): boolean {
  return metadata[key] === true;
}

function formatSourceType(sourceType: string | null): string {
  if (sourceType === null) {
    return "Manual";
  }

  if (sourceType === "github") {
    return "GitHub";
  }

  return sourceType.charAt(0).toUpperCase() + sourceType.slice(1);
}

const cardClassName = computed(() => ({
  "k-card--manual": props.card.sourceType === null,
  "k-card--synced": props.card.sourceType !== null,
  "k-card--dragging": props.isDragging,
  "k-card--archived": props.card.archivedAt !== null,
}));

const isManualCard = computed(() => props.card.sourceType === null);
const isGitHubCard = computed(() => props.card.sourceType === "github");
const sourceLabel = computed(() => {
  return isManualCard.value ? "Manual" : `${formatSourceType(props.card.sourceType)} sync`;
});

const metadataObject = computed(() => parseMetadataObject(props.card.metadata));
const githubMetadata = computed<GitHubCardMetadata | null>(() => {
  if (!isGitHubCard.value || metadataObject.value === null) {
    return null;
  }

  return {
    number: getMetadataNumber(metadataObject.value, "number"),
    state: getMetadataString(metadataObject.value, "state"),
    labels: getMetadataLabels(metadataObject.value, "labels"),
    assignee: getMetadataString(metadataObject.value, "assignee"),
    htmlUrl: getMetadataString(metadataObject.value, "html_url"),
    updatedAt: getMetadataString(metadataObject.value, "updated_at"),
    stale: getMetadataBoolean(metadataObject.value, "stale"),
  };
});
const githubIssueLabel = computed(() => {
  const issueNumber = githubMetadata.value?.number;
  return issueNumber === null || issueNumber === undefined ? "GitHub issue" : `Issue #${issueNumber}`;
});
const githubUpdatedLabel = computed(() => {
  const updatedAt = githubMetadata.value?.updatedAt;
  return updatedAt ? formatRelativeTime(updatedAt) : null;
});
const metadataText = computed(() => {
  if (props.card.metadata === null) {
    return null;
  }

  return metadataObject.value === null ? props.card.metadata : null;
});
const sourceDetail = computed(() => props.card.sourceKey ?? "No source key");
const updatedLabel = computed(() => formatRelativeTime(props.card.updatedAt));
const createdLabel = computed(() => formatRelativeTime(props.card.createdAt));

function beginRename(): void {
  isEditing.value = true;
  titleDraft.value = props.card.title;
}

function cancelRename(): void {
  isEditing.value = false;
  titleDraft.value = "";
}

function submitRename(): void {
  const title = titleDraft.value.trim();
  if (title.length === 0) {
    cancelRename();
    return;
  }

  if (title !== props.card.title) {
    emit("rename", title);
  }

  cancelRename();
}

function handleDelete(): void {
  if (!window.confirm(`Delete card “${props.card.title}”?`)) {
    return;
  }

  emit("delete");
}

function handleArchive(): void {
  emit("archive");
}

function handleDragStart(event: DragEvent): void {
  if (event.dataTransfer) {
    event.dataTransfer.effectAllowed = "move";
    event.dataTransfer.setData("text/plain", props.card.id);
  }

  emit("dragStart");
}

function handleDragEnd(): void {
  emit("dragEnd");
}
</script>

<template>
  <article
    class="k-card"
    :class="cardClassName"
    :draggable="!isMutating"
    @dragstart="handleDragStart"
    @dragend="handleDragEnd"
  >
    <div class="k-card__meta-row">
      <div class="k-card__badges">
        <span class="k-card__source-pill">
          {{ sourceLabel }}
        </span>
        <span
          v-if="isGitHubCard"
          class="k-card__sync-pill"
        >
          <Github
            :size="12"
            aria-hidden="true"
          />
          {{ githubIssueLabel }}
        </span>
        <span
          v-if="githubMetadata?.stale"
          class="k-card__stale-pill"
          role="status"
          aria-label="Sync is stale"
        >
          <TriangleAlert
            :size="12"
            aria-hidden="true"
          />
          Stale
        </span>
      </div>
      <span class="k-card__timestamp">
        Updated {{ updatedLabel }}
      </span>
    </div>

    <form
      v-if="isEditing"
      class="k-card__rename-form"
      @submit.prevent="submitRename"
    >
      <input
        v-model="titleDraft"
        class="k-card__rename-input"
        type="text"
        maxlength="240"
        :disabled="isMutating"
        @keydown.esc.prevent="cancelRename"
      >
    </form>

    <h3
      v-else
      class="k-card__title"
    >
      {{ card.title }}
    </h3>

    <div
      v-if="isGitHubCard"
      class="k-card__sync-summary"
    >
      <a
        v-if="githubMetadata?.htmlUrl"
        class="k-card__github-link"
        :href="githubMetadata.htmlUrl"
        target="_blank"
        rel="noreferrer noopener"
        :aria-label="`Open ${githubIssueLabel} on GitHub`"
      >
        <Github
          :size="14"
          aria-hidden="true"
        />
        <span>{{ githubIssueLabel }}</span>
        <ExternalLink
          :size="12"
          aria-hidden="true"
        />
      </a>

      <dl class="k-card__sync-details">
        <div
          v-if="githubMetadata?.state"
          class="k-card__detail"
        >
          <dt>State</dt>
          <dd>{{ githubMetadata.state }}</dd>
        </div>
        <div
          v-if="githubMetadata?.assignee"
          class="k-card__detail"
        >
          <dt>Assignee</dt>
          <dd>{{ githubMetadata.assignee }}</dd>
        </div>
        <div
          v-if="githubUpdatedLabel"
          class="k-card__detail"
        >
          <dt>GitHub updated</dt>
          <dd>{{ githubUpdatedLabel }}</dd>
        </div>
      </dl>

      <ul
        v-if="githubMetadata && githubMetadata.labels.length > 0"
        class="k-card__label-list"
        aria-label="GitHub labels"
      >
        <li
          v-for="label in githubMetadata.labels"
          :key="label"
          class="k-card__label"
        >
          {{ label }}
        </li>
      </ul>
    </div>

    <dl class="k-card__details">
      <div class="k-card__detail">
        <dt>Created</dt>
        <dd>{{ createdLabel }}</dd>
      </div>
      <div class="k-card__detail">
        <dt>Source key</dt>
        <dd>{{ sourceDetail }}</dd>
      </div>
      <div class="k-card__detail">
        <dt>Position</dt>
        <dd>{{ card.position }}</dd>
      </div>
    </dl>

    <p
      v-if="metadataText"
      class="k-card__metadata"
    >
      {{ metadataText }}
    </p>

    <div class="k-card__actions">
      <button
        v-if="isEditing"
        type="button"
        class="k-card__action"
        :disabled="isMutating"
        @click="submitRename"
      >
        Save
      </button>
      <button
        v-else
        type="button"
        class="k-card__action"
        :disabled="isMutating"
        @click="beginRename"
      >
        Rename
      </button>
      <button
        type="button"
        class="k-card__action"
        :disabled="isMutating"
        @click="handleArchive"
      >
        Archive
      </button>
      <button
        type="button"
        class="k-card__action k-card__action--danger"
        :disabled="isMutating"
        @click="handleDelete"
      >
        Delete
      </button>
    </div>
  </article>
</template>

<style scoped>
.k-card {
  display: flex;
  flex-direction: column;
  gap: 10px;
  padding: 12px;
  border: 1px solid var(--border);
  border-radius: var(--radius-card);
  background: var(--card-bg);
  cursor: grab;
  transition: transform 0.2s ease, box-shadow 0.2s ease, opacity 0.2s ease;
}

.k-card:hover {
  transform: translateY(-2px);
  box-shadow: 0 12px 24px rgba(0, 0, 0, 0.18);
}

.k-card--manual {
  border-left: 3px solid var(--accent);
  background: color-mix(in srgb, var(--card-bg) 92%, var(--accent-dim) 8%);
}

.k-card--synced {
  border-left: 3px solid color-mix(in srgb, var(--muted) 55%, var(--border));
  background: color-mix(in srgb, var(--card-bg) 94%, var(--muted) 6%);
}

.k-card--dragging {
  opacity: 0.45;
  transform: rotate(1deg);
}

.k-card__meta-row,
.k-card__actions {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
}

.k-card__badges {
  display: inline-flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 6px;
}

.k-card__source-pill {
  display: inline-flex;
  align-items: center;
  padding: 4px 8px;
  border-radius: 999px;
  background: rgba(255, 255, 255, 0.06);
  font-size: 10px;
  font-weight: 700;
  letter-spacing: 0.04em;
  text-transform: uppercase;
  color: var(--muted);
}

.k-card--manual .k-card__source-pill {
  background: color-mix(in srgb, var(--accent) 18%, transparent);
  color: var(--accent);
}

.k-card--synced .k-card__source-pill {
  background: color-mix(in srgb, var(--muted) 30%, transparent);
  color: var(--text);
}

.k-card__sync-pill,
.k-card__stale-pill,
.k-card__github-link {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  border-radius: 999px;
  font-size: 10px;
  font-weight: 600;
}

.k-card__sync-pill,
.k-card__stale-pill {
  padding: 4px 8px;
}

.k-card__sync-pill {
  background: color-mix(in srgb, var(--card-bg) 76%, var(--muted) 24%);
  color: var(--text);
}

.k-card__stale-pill {
  background: color-mix(in srgb, #f59e0b 18%, transparent);
  color: #fbbf24;
}

.k-card__timestamp,
.k-card__metadata,
.k-card__detail dt,
.k-card__detail dd {
  font-size: 11px;
  color: var(--muted);
}

.k-card__title {
  margin: 0;
  font-size: 13px;
  font-weight: 700;
  line-height: 1.4;
  color: var(--text);
}

.k-card__sync-summary {
  display: flex;
  flex-direction: column;
  gap: 10px;
  padding: 10px;
  border: 1px solid color-mix(in srgb, var(--border) 84%, transparent);
  border-radius: var(--radius-btn);
  background: color-mix(in srgb, var(--card-bg) 90%, var(--muted) 10%);
}

.k-card__github-link {
  align-self: flex-start;
  padding: 6px 10px;
  border: 1px solid color-mix(in srgb, var(--border) 92%, transparent);
  background: color-mix(in srgb, var(--card-bg) 88%, var(--muted) 12%);
  color: var(--text);
  text-decoration: none;
  transition: border-color 0.2s ease, background-color 0.2s ease, color 0.2s ease;
}

.k-card__github-link:hover {
  border-color: color-mix(in srgb, var(--accent) 24%, var(--border));
  background: color-mix(in srgb, var(--card-bg) 82%, var(--accent-dim) 18%);
}

.k-card__sync-details {
  display: grid;
  grid-template-columns: repeat(3, minmax(0, 1fr));
  gap: 8px;
  margin: 0;
}

.k-card__label-list {
  display: flex;
  flex-wrap: wrap;
  gap: 6px;
  margin: 0;
  padding: 0;
  list-style: none;
}

.k-card__label {
  padding: 4px 8px;
  border-radius: 999px;
  background: color-mix(in srgb, var(--accent-dim) 55%, transparent);
  color: var(--text);
  font-size: 10px;
}

.k-card__rename-form {
  display: flex;
}

.k-card__rename-input {
  width: 100%;
  border: 1px solid var(--border);
  border-radius: var(--radius-btn);
  background: rgba(255, 255, 255, 0.02);
  color: var(--text);
  padding: 8px 10px;
}

.k-card__details {
  display: grid;
  grid-template-columns: repeat(3, minmax(0, 1fr));
  gap: 8px;
  margin: 0;
}

.k-card__detail dt,
.k-card__detail dd {
  margin: 0;
}

.k-card__detail dt {
  font-size: 10px;
  font-weight: 700;
  letter-spacing: 0.04em;
  text-transform: uppercase;
}

.k-card__detail dd {
  margin-top: 4px;
  color: var(--text);
}

.k-card__metadata {
  margin: 0;
  line-height: 1.5;
}

.k-card__action {
  border: 1px solid var(--border);
  border-radius: var(--radius-btn);
  background: transparent;
  color: var(--text);
  font-size: 11px;
  font-weight: 600;
  padding: 6px 9px;
  cursor: pointer;
}

.k-card__action:disabled {
  cursor: not-allowed;
  opacity: 0.6;
}

.k-card__action--danger {
  color: #fca5a5;
}
</style>
