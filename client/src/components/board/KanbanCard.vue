<script setup lang="ts">
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

const isEditing = shallowRef(false);
const titleDraft = shallowRef("");

const cardClassName = computed(() => ({
  "k-card--manual": props.card.sourceType === null,
  "k-card--dragging": props.isDragging,
  "k-card--archived": props.card.archivedAt !== null,
}));

const sourceLabel = computed(() => {
  if (props.card.sourceType === null) {
    return "Manual";
  }

  return props.card.sourceType;
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
      <span class="k-card__source-pill">
        {{ sourceLabel }}
      </span>
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
      v-if="card.metadata"
      class="k-card__metadata"
    >
      {{ card.metadata }}
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

.k-card__timestamp,
.k-card__metadata,
.k-card__detail dt,
.k-card__detail dd {
  font-size: 12px;
  color: var(--muted);
}

.k-card__title {
  margin: 0;
  font-size: 14px;
  font-weight: 700;
  line-height: 1.4;
  color: var(--text);
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
  font-size: 12px;
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
