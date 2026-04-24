<script setup lang="ts">
import type { BoardCard, BoardLane } from "@/stores/board";
import { computed, shallowRef } from "vue";
import KanbanCard from "@/components/board/KanbanCard.vue";

interface CardDraftPayload {
  laneId: string;
  title: string;
}

interface LaneDraftPayload {
  laneId: string;
  name: string;
}

interface DropCardPayload {
  laneId: string;
  index: number;
}

interface Props {
  lane: BoardLane;
  cards: readonly BoardCard[];
  draggedCardId: string | null;
  isMutating: boolean;
  canMoveLeft: boolean;
  canMoveRight: boolean;
}

interface Emits {
  createCard: [payload: CardDraftPayload];
  renameLane: [payload: LaneDraftPayload];
  deleteLane: [laneId: string];
  moveLaneLeft: [laneId: string];
  moveLaneRight: [laneId: string];
  setInboxLane: [laneId: string];
  renameCard: [cardId: string, title: string];
  deleteCard: [cardId: string];
  archiveCard: [cardId: string];
  startCardDrag: [cardId: string, laneId: string];
  endCardDrag: [];
  dropCard: [payload: DropCardPayload];
}

const props = defineProps<Props>();
const emit = defineEmits<Emits>();

const isCreatingCard = shallowRef(false);
const cardDraft = shallowRef("");
const isEditingLane = shallowRef(false);
const laneDraft = shallowRef("");
const dragEnterCount = shallowRef(0);
const activeDropIndex = shallowRef<number | null>(null);

const cardCountLabel = computed(() => `${props.cards.length} card${props.cards.length === 1 ? "" : "s"}`);
const emptyLabel = computed(() => props.lane.isInbox ? "Inbox is empty." : "Drop a card here or add one below.");
const hasDraggedCard = computed(() => props.draggedCardId !== null);

function beginCreateCard(): void {
  isCreatingCard.value = true;
}

function cancelCreateCard(): void {
  isCreatingCard.value = false;
  cardDraft.value = "";
}

function beginLaneRename(): void {
  isEditingLane.value = true;
  laneDraft.value = props.lane.name;
}

function cancelLaneRename(): void {
  isEditingLane.value = false;
  laneDraft.value = "";
}

function handleDragOver(event: DragEvent, index: number): void {
  if (!hasDraggedCard.value) {
    return;
  }

  event.preventDefault();
  if (event.dataTransfer) {
    event.dataTransfer.dropEffect = "move";
  }

  activeDropIndex.value = index;
}

function handleDragEnter(index: number): void {
  if (!hasDraggedCard.value) {
    return;
  }

  dragEnterCount.value += 1;
  activeDropIndex.value = index;
}

function handleDragLeave(): void {
  if (!hasDraggedCard.value) {
    return;
  }

  dragEnterCount.value = Math.max(0, dragEnterCount.value - 1);
  if (dragEnterCount.value === 0) {
    activeDropIndex.value = null;
  }
}

function handleColumnDrop(event: DragEvent, index: number): void {
  dragEnterCount.value = 0;
  activeDropIndex.value = null;
  if (!hasDraggedCard.value) {
    return;
  }

  event.preventDefault();
  emit("dropCard", {
    laneId: props.lane.id,
    index,
  });
}

function handleCardDragStart(cardId: string): void {
  emit("startCardDrag", cardId, props.lane.id);
}

function handleCardDragEnd(): void {
  dragEnterCount.value = 0;
  activeDropIndex.value = null;
  emit("endCardDrag");
}

async function submitCreateCard(): Promise<void> {
  const title = cardDraft.value.trim();
  if (title.length === 0) {
    return;
  }

  emit("createCard", {
    laneId: props.lane.id,
    title,
  });
  cancelCreateCard();
}

async function submitLaneRename(): Promise<void> {
  const name = laneDraft.value.trim();
  if (name.length === 0 || name === props.lane.name) {
    cancelLaneRename();
    return;
  }

  emit("renameLane", {
    laneId: props.lane.id,
    name,
  });
  cancelLaneRename();
}

function handleDeleteLane(): void {
  const hasCards = props.cards.length > 0;
  const confirmation = window.confirm(
    hasCards
      ? `Delete lane “${props.lane.name}”? Existing cards in this lane will also be removed if the API does not migrate them.`
      : `Delete lane “${props.lane.name}”?`,
  );

  if (!confirmation) {
    return;
  }

  emit("deleteLane", props.lane.id);
}

function handleSetInboxLane(): void {
  if (props.lane.isInbox) {
    return;
  }

  emit("setInboxLane", props.lane.id);
}

function showDropSlot(index: number): boolean {
  return hasDraggedCard.value && activeDropIndex.value === index;
}
</script>

<template>
  <section
    class="kanban-col"
    :aria-label="`${lane.name} column`"
  >
    <div class="kanban-col__surface">
      <div class="kanban-col__header">
        <div class="kanban-col__title-wrap">
          <div class="kanban-col__eyebrow-row">
            <span
              v-if="lane.isInbox"
              class="kanban-col__inbox-pill"
            >
              Inbox
            </span>
            <span class="kanban-col__count">{{ cardCountLabel }}</span>
          </div>

          <form
            v-if="isEditingLane"
            class="kanban-col__rename-form"
            @submit.prevent="submitLaneRename"
          >
            <input
              v-model="laneDraft"
              class="kanban-col__rename-input"
              type="text"
              maxlength="120"
              :disabled="isMutating"
              @keydown.esc.prevent="cancelLaneRename"
            >
          </form>

          <h2
            v-else
            class="kanban-col__title"
          >
            {{ lane.name }}
          </h2>
        </div>

        <div class="kanban-col__lane-actions">
          <button
            type="button"
            class="kanban-col__action"
            :disabled="isMutating || !canMoveLeft"
            @click="emit('moveLaneLeft', lane.id)"
          >
            ←
          </button>
          <button
            type="button"
            class="kanban-col__action"
            :disabled="isMutating || !canMoveRight"
            @click="emit('moveLaneRight', lane.id)"
          >
            →
          </button>
          <button
            v-if="isEditingLane"
            type="button"
            class="kanban-col__action"
            :disabled="isMutating"
            @click="submitLaneRename"
          >
            Save
          </button>
          <button
            v-else
            type="button"
            class="kanban-col__action"
            :disabled="isMutating"
            @click="beginLaneRename"
          >
            Rename
          </button>
          <button
            type="button"
            class="kanban-col__action"
            :disabled="isMutating || lane.isInbox"
            @click="handleSetInboxLane"
          >
            {{ lane.isInbox ? 'Inbox' : 'Make inbox' }}
          </button>
          <button
            type="button"
            class="kanban-col__action kanban-col__action--danger"
            :disabled="isMutating"
            @click="handleDeleteLane"
          >
            Delete
          </button>
        </div>
      </div>

      <div
        class="kanban-col__cards"
        :class="{ 'kanban-col__cards--drop-target': dragEnterCount > 0 }"
      >
        <div
          class="kanban-col__drop-slot"
          :class="{ 'kanban-col__drop-slot--active': showDropSlot(0) }"
          @dragover="handleDragOver($event, 0)"
          @dragenter="handleDragEnter(0)"
          @dragleave="handleDragLeave"
          @drop="handleColumnDrop($event, 0)"
        />

        <template
          v-for="(card, index) in cards"
          :key="card.id"
        >
          <KanbanCard
            :card="card"
            :is-dragging="draggedCardId === card.id"
            :is-mutating="isMutating"
            @rename="emit('renameCard', card.id, $event)"
            @delete="emit('deleteCard', card.id)"
            @archive="emit('archiveCard', card.id)"
            @drag-start="handleCardDragStart(card.id)"
            @drag-end="handleCardDragEnd"
          />

          <div
            class="kanban-col__drop-slot"
            :class="{ 'kanban-col__drop-slot--active': showDropSlot(index + 1) }"
            @dragover="handleDragOver($event, index + 1)"
            @dragenter="handleDragEnter(index + 1)"
            @dragleave="handleDragLeave"
            @drop="handleColumnDrop($event, index + 1)"
          />
        </template>

        <p
          v-if="cards.length === 0"
          class="kanban-col__empty"
        >
          {{ emptyLabel }}
        </p>
      </div>

      <div class="kanban-col__composer">
        <form
          v-if="isCreatingCard"
          class="kanban-col__composer-form"
          @submit.prevent="submitCreateCard"
        >
            <input
              v-model="cardDraft"
              class="kanban-col__composer-input"
              type="text"
              maxlength="240"
              placeholder="Add a card title"
              :disabled="isMutating"
              @keydown.esc.prevent="cancelCreateCard"
            >
          <div class="kanban-col__composer-actions">
              <button
                type="submit"
                class="kanban-col__composer-button kanban-col__composer-button--primary"
                :disabled="isMutating || cardDraft.trim().length === 0"
              >
                Add card
            </button>
            <button
              type="button"
              class="kanban-col__composer-button"
              :disabled="isMutating"
              @click="cancelCreateCard"
            >
              Cancel
            </button>
          </div>
        </form>

        <button
          v-else
          type="button"
          class="kanban-col__composer-button kanban-col__composer-button--ghost"
          :disabled="isMutating"
          @click="beginCreateCard"
        >
          + Add a card
        </button>
      </div>
    </div>
  </section>
</template>

<style scoped>
.kanban-col {
  min-width: 320px;
  max-width: 320px;
  display: flex;
  flex-direction: column;
  min-height: 0;
}

.kanban-col__surface {
  display: flex;
  flex: 1;
  flex-direction: column;
  gap: 12px;
  min-height: 0;
  padding: 12px;
  border: 1px solid var(--border);
  border-radius: var(--radius-card);
  background: rgba(255, 255, 255, 0.03);
}

.kanban-col__header,
.kanban-col__title-wrap {
  display: flex;
  flex-direction: column;
  gap: 8px;
}

.kanban-col__eyebrow-row,
.kanban-col__lane-actions,
.kanban-col__composer-actions {
  display: flex;
  flex-wrap: wrap;
  gap: 6px;
  align-items: center;
}

.kanban-col__title {
  margin: 0;
  font-size: 16px;
  font-weight: 700;
  color: var(--text);
}

.kanban-col__rename-form {
  display: flex;
}

.kanban-col__rename-input,
.kanban-col__composer-input {
  width: 100%;
  border: 1px solid var(--border);
  border-radius: var(--radius-btn);
  background: var(--card-bg);
  color: var(--text);
  padding: 9px 10px;
}

.kanban-col__count,
.kanban-col__inbox-pill {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  min-height: 24px;
  padding: 0 8px;
  border-radius: 999px;
  font-size: 11px;
  font-weight: 700;
  letter-spacing: 0.04em;
  text-transform: uppercase;
}

.kanban-col__count {
  border: 1px solid var(--border);
  color: var(--muted);
}

.kanban-col__inbox-pill {
  background: var(--accent-dim);
  color: var(--accent);
}

.kanban-col__action,
.kanban-col__composer-button {
  border: 1px solid var(--border);
  border-radius: var(--radius-btn);
  background: var(--card-bg);
  color: var(--text);
  font-size: 12px;
  font-weight: 600;
  padding: 7px 10px;
  cursor: pointer;
}

.kanban-col__action:disabled,
.kanban-col__composer-button:disabled {
  cursor: not-allowed;
  opacity: 0.6;
}

.kanban-col__action--danger {
  color: #fca5a5;
}

.kanban-col__cards {
  display: flex;
  flex: 1;
  flex-direction: column;
  min-height: 180px;
  overflow-y: auto;
  padding-right: 4px;
}

.kanban-col__cards--drop-target {
  border-radius: var(--radius-card);
  background: rgba(59, 130, 246, 0.06);
}

.kanban-col__drop-slot {
  flex: 0 0 auto;
  height: 8px;
  margin: 4px 0;
  border-radius: 999px;
  transition: background-color 0.2s ease, height 0.2s ease;
}

.kanban-col__drop-slot--active {
  height: 18px;
  background: rgba(59, 130, 246, 0.35);
}

.kanban-col__empty {
  margin: 0;
  padding: 18px 14px;
  border: 1px dashed var(--border);
  border-radius: var(--radius-card);
  background: rgba(255, 255, 255, 0.02);
  font-size: 12px;
  text-align: center;
  color: var(--muted);
}

.kanban-col__composer {
  display: flex;
  flex-direction: column;
  gap: 8px;
}

.kanban-col__composer-form {
  display: flex;
  flex-direction: column;
  gap: 8px;
}

.kanban-col__composer-button--primary {
  border-color: var(--accent);
  background: var(--accent);
  color: #fff;
}

.kanban-col__composer-button--ghost {
  border-style: dashed;
}
</style>
