<script setup lang="ts">
import type { BoardSyncResult } from "@/lib/board-api";
import type { BoardLaneWithCards } from "@/stores/board";
import { computed, onBeforeUnmount, shallowRef } from "vue";
import { storeToRefs } from "pinia";
import BoardSourceConfig from "@/components/board/BoardSourceConfig.vue";
import KanbanColumn from "@/components/board/KanbanColumn.vue";
import { useBoardStore } from "@/stores/board";

interface CardDraftPayload {
  laneId: string;
  title: string;
}

interface LaneDraftPayload {
  laneId: string;
  name: string;
}

interface DraggedCardState {
  cardId: string;
  laneId: string;
}

interface DropCardPayload {
  laneId: string;
  index: number;
}

interface SyncFeedback {
  title: string;
  message: string;
}

const boardStore = useBoardStore();

const {
  activeCards,
  board,
  cardsByLaneId,
  error,
  hasBoard,
  inboxLane,
  isLoaded,
  isLoading,
  isMutating,
  lanesWithCards,
} = storeToRefs(boardStore);

const isAddingLane = shallowRef(false);
const laneDraft = shallowRef("");
const isEditingBoard = shallowRef(false);
const boardNameDraft = shallowRef("");
const draggedCard = shallowRef<DraggedCardState | null>(null);
const isSyncing = shallowRef(false);
const syncFeedback = shallowRef<SyncFeedback | null>(null);

let syncFeedbackTimer: ReturnType<typeof setTimeout> | null = null;

const boardTitle = computed(() => board.value?.name ?? "Kanban Board");

const subtitle = computed(() => {
  if (isLoading.value && !isLoaded.value) {
    return "Loading your persisted board…";
  }

  if (!hasBoard.value) {
    return "Create your first lane to start a persistent board for manual and API-backed cards.";
  }

  if (inboxLane.value !== null) {
    return `${activeCards.value.length} active cards across ${lanesWithCards.value.length} lanes. Inbox lane: ${inboxLane.value.name}.`;
  }

  return `${activeCards.value.length} active cards across ${lanesWithCards.value.length} lanes. Choose an inbox lane to capture manual work quickly.`;
});

const summaryItems = computed(() => {
  const manualCards = activeCards.value.filter((card) => card.sourceType === null).length;

  return [
    {
      label: "Lanes",
      value: lanesWithCards.value.length.toString(),
    },
    {
      label: "Cards",
      value: activeCards.value.length.toString(),
    },
    {
      label: "Manual",
      value: manualCards.toString(),
    },
    {
      label: "Inbox",
      value: inboxLane.value?.name ?? "Unset",
    },
  ] as const;
});

const showLoadingState = computed(() => isLoading.value && !isLoaded.value);
const showEmptyState = computed(() => isLoaded.value && lanesWithCards.value.length === 0);
const syncButtonLabel = computed(() => isSyncing.value ? "Syncing…" : "Sync now");

onBeforeUnmount(() => {
  clearSyncFeedbackTimer();
});

function clearSyncFeedbackTimer(): void {
  if (syncFeedbackTimer !== null) {
    clearTimeout(syncFeedbackTimer);
    syncFeedbackTimer = null;
  }
}

function showSyncFeedback(result: BoardSyncResult): void {
  syncFeedback.value = {
    title: "Board synced",
    message: `${result.cardsCreated} added, ${result.cardsUpdated} updated, ${result.cardsMarkedStale} stale`,
  };

  clearSyncFeedbackTimer();
  syncFeedbackTimer = setTimeout(() => {
    syncFeedback.value = null;
    syncFeedbackTimer = null;
  }, 5_000);
}

function openAddLaneForm(): void {
  isAddingLane.value = true;
}

function cancelAddLane(): void {
  isAddingLane.value = false;
  laneDraft.value = "";
}

function beginBoardRename(): void {
  if (board.value === null) {
    return;
  }

  isEditingBoard.value = true;
  boardNameDraft.value = board.value.name;
}

function cancelBoardRename(): void {
  isEditingBoard.value = false;
  boardNameDraft.value = "";
}

async function handleCreateLane(): Promise<void> {
  const name = laneDraft.value.trim();
  if (name.length === 0) {
    return;
  }

  try {
    await boardStore.createLane({ name });
    cancelAddLane();
  } catch {
    // Store error state is surfaced in the UI.
  }
}

async function handleRenameBoard(): Promise<void> {
  const name = boardNameDraft.value.trim();
  if (name.length === 0 || name === board.value?.name) {
    cancelBoardRename();
    return;
  }

  try {
    await boardStore.renameBoard(name);
    cancelBoardRename();
  } catch {
    // Store error state is surfaced in the UI.
  }
}

async function handleRetryLoad(): Promise<void> {
  try {
    await boardStore.reloadBoard();
  } catch {
    // Store error state is surfaced in the UI.
  }
}

function handleDismissError(): void {
  boardStore.clearError();
}

function handleDismissSyncFeedback(): void {
  clearSyncFeedbackTimer();
  syncFeedback.value = null;
}

async function handleSyncBoard(): Promise<void> {
  if (!hasBoard.value || isSyncing.value) {
    return;
  }

  isSyncing.value = true;

  try {
    const result = await boardStore.syncBoard();
    showSyncFeedback(result);
  } catch {
    // Store error state is surfaced in the UI.
  } finally {
    isSyncing.value = false;
  }
}

async function handleCreateCard(payload: CardDraftPayload): Promise<void> {
  try {
    await boardStore.createCard({
      laneId: payload.laneId,
      title: payload.title,
    });
  } catch {
    // Store error state is surfaced in the UI.
  }
}

async function handleRenameLane(payload: LaneDraftPayload): Promise<void> {
  try {
    await boardStore.renameLane(payload.laneId, payload.name);
  } catch {
    // Store error state is surfaced in the UI.
  }
}

async function handleDeleteLane(laneId: string): Promise<void> {
  try {
    await boardStore.deleteLane(laneId);
  } catch {
    // Store error state is surfaced in the UI.
  }
}

async function handleSetInboxLane(laneId: string): Promise<void> {
  try {
    await boardStore.setInboxLane(laneId);
  } catch {
    // Store error state is surfaced in the UI.
  }
}

async function handleMoveLane(laneId: string, direction: "left" | "right"): Promise<void> {
  const nextLaneIds = lanesWithCards.value.map((entry) => entry.lane.id);
  const currentIndex = nextLaneIds.indexOf(laneId);
  if (currentIndex < 0) {
    return;
  }

  const targetIndex = direction === "left" ? currentIndex - 1 : currentIndex + 1;
  if (targetIndex < 0 || targetIndex >= nextLaneIds.length) {
    return;
  }

  [nextLaneIds[currentIndex], nextLaneIds[targetIndex]] = [nextLaneIds[targetIndex], nextLaneIds[currentIndex]];

  try {
    await boardStore.reorderLanes(nextLaneIds);
  } catch {
    // Store error state is surfaced in the UI.
  }
}

async function handleRenameCard(cardId: string, title: string): Promise<void> {
  try {
    await boardStore.renameCard(cardId, title);
  } catch {
    // Store error state is surfaced in the UI.
  }
}

async function handleDeleteCard(cardId: string): Promise<void> {
  try {
    await boardStore.deleteCard(cardId);
  } catch {
    // Store error state is surfaced in the UI.
  }
}

async function handleArchiveCard(cardId: string): Promise<void> {
  try {
    await boardStore.archiveCard(cardId);
  } catch {
    // Store error state is surfaced in the UI.
  }
}

function handleCardDragStart(cardId: string, laneId: string): void {
  draggedCard.value = {
    cardId,
    laneId,
  };
}

function handleCardDragEnd(): void {
  draggedCard.value = null;
}

async function handleDropCard(payload: DropCardPayload): Promise<void> {
  const currentDrag = draggedCard.value;
  if (currentDrag === null) {
    return;
  }

  const sourceCards = cardsByLaneId.value.get(currentDrag.laneId) ?? [];
  const currentIndex = sourceCards.findIndex((card) => card.id === currentDrag.cardId);
  let nextIndex = payload.index;

  if (currentDrag.laneId === payload.laneId && currentIndex >= 0 && currentIndex < payload.index) {
    nextIndex -= 1;
  }

  if (currentDrag.laneId === payload.laneId && currentIndex === nextIndex) {
    draggedCard.value = null;
    return;
  }

  try {
    await boardStore.moveCard(currentDrag.cardId, {
      laneId: payload.laneId,
      position: nextIndex,
    });
  } catch {
    // Store error state is surfaced in the UI.
  } finally {
    draggedCard.value = null;
  }
}

function isLaneMoveEnabled(entries: readonly BoardLaneWithCards[], index: number, direction: "left" | "right"): boolean {
  return direction === "left" ? index > 0 : index < entries.length - 1;
}

</script>

<template>
  <section
    class="kanban-container"
    aria-label="Kanban board"
  >
    <header class="kanban-header">
      <div class="kanban-header__copy">
        <p class="kanban-header__eyebrow">
          Persistent board
        </p>
        <div>
          <form
            v-if="isEditingBoard"
            class="kanban-header__rename-form"
            @submit.prevent="handleRenameBoard"
          >
            <input
              v-model="boardNameDraft"
              class="kanban-header__rename-input"
              type="text"
              maxlength="120"
              placeholder="Rename board"
              :disabled="isMutating"
              @keydown.esc.prevent="cancelBoardRename"
            >
            <div class="kanban-header__rename-actions">
              <button
                type="submit"
                class="kanban-header__rename-button kanban-header__rename-button--primary"
                :disabled="isMutating || boardNameDraft.trim().length === 0"
              >
                Save board
              </button>
              <button
                type="button"
                class="kanban-header__rename-button"
                :disabled="isMutating"
                @click="cancelBoardRename"
              >
                Cancel
              </button>
            </div>
          </form>

          <h1
            v-else
            class="kanban-header__title"
          >
            {{ boardTitle }}
          </h1>
          <p class="kanban-header__subtitle">
            {{ subtitle }}
          </p>
        </div>
      </div>

      <div class="kanban-header__actions">
        <button
          v-if="hasBoard"
          type="button"
          class="kanban-header__button"
          data-testid="kanban-sync-button"
          :disabled="showLoadingState || isMutating"
          @click="handleSyncBoard"
        >
          {{ syncButtonLabel }}
        </button>

        <button
          v-if="hasBoard && !isEditingBoard"
          type="button"
          class="kanban-header__button"
          :disabled="showLoadingState || isMutating"
          @click="beginBoardRename"
        >
          Rename board
        </button>

        <button
          type="button"
          class="kanban-header__button"
          :disabled="showLoadingState"
          @click="openAddLaneForm"
        >
          Add Lane
        </button>
      </div>
    </header>

    <section
      class="kanban-summary"
      aria-label="Board summary"
    >
      <div
        v-for="item in summaryItems"
        :key="item.label"
        class="kanban-summary__item"
      >
        <span class="kanban-summary__value">{{ item.value }}</span>
        <span class="kanban-summary__label">{{ item.label }}</span>
      </div>
    </section>

    <BoardSourceConfig :board-id="board?.id ?? null" />

    <section
      v-if="isAddingLane"
      class="kanban-lane-creator"
      aria-label="Create lane"
    >
      <form
        class="kanban-lane-creator__form"
        @submit.prevent="handleCreateLane"
      >
        <input
          v-model="laneDraft"
          class="kanban-lane-creator__input"
          type="text"
          maxlength="120"
          placeholder="Add a lane name"
          :disabled="isMutating"
        >
        <div class="kanban-lane-creator__actions">
          <button
            type="submit"
            class="kanban-lane-creator__button kanban-lane-creator__button--primary"
            :disabled="isMutating || laneDraft.trim().length === 0"
          >
            Save lane
          </button>
          <button
            type="button"
            class="kanban-lane-creator__button"
            :disabled="isMutating"
            @click="cancelAddLane"
          >
            Cancel
          </button>
        </div>
      </form>
    </section>

    <div
      v-if="syncFeedback"
      class="kanban-toast"
      role="status"
      aria-live="polite"
      data-testid="kanban-sync-feedback"
    >
      <div class="kanban-toast__copy">
        <p class="kanban-toast__title">
          {{ syncFeedback.title }}
        </p>
        <p class="kanban-toast__message">
          {{ syncFeedback.message }}
        </p>
      </div>
      <button
        type="button"
        class="kanban-toast__button"
        aria-label="Dismiss sync feedback"
        @click="handleDismissSyncFeedback"
      >
        Dismiss
      </button>
    </div>

    <div
      v-if="error"
      class="kanban-banner"
      role="alert"
    >
      <p class="kanban-banner__copy">
        {{ error }}
      </p>
      <div class="kanban-banner__actions">
        <button
          type="button"
          class="kanban-banner__button kanban-banner__button--primary"
          @click="handleRetryLoad"
        >
          Retry
        </button>
        <button
          type="button"
          class="kanban-banner__button"
          @click="handleDismissError"
        >
          Dismiss
        </button>
      </div>
    </div>

    <section
      v-if="showLoadingState"
      class="kanban-state"
      aria-live="polite"
    >
      <p class="kanban-state__title">
        Loading board…
      </p>
      <p class="kanban-state__copy">
        Fetching lanes and cards from the persisted board store.
      </p>
    </section>

    <section
      v-else-if="showEmptyState"
      class="kanban-state"
      aria-live="polite"
    >
      <p class="kanban-state__title">
        No lanes yet
      </p>
      <p class="kanban-state__copy">
        Add a lane to create your first board column, then add cards directly inside it.
      </p>
      <button
        type="button"
        class="kanban-state__button"
        @click="openAddLaneForm"
      >
        Create first lane
      </button>
    </section>

    <div
      v-else
      class="kanban-board"
    >
      <KanbanColumn
        v-for="(entry, index) in lanesWithCards"
        :key="entry.lane.id"
        :lane="entry.lane"
        :cards="entry.cards"
        :dragged-card-id="draggedCard?.cardId ?? null"
        :is-mutating="isMutating"
        :can-move-left="isLaneMoveEnabled(lanesWithCards, index, 'left')"
        :can-move-right="isLaneMoveEnabled(lanesWithCards, index, 'right')"
        @create-card="handleCreateCard"
        @rename-lane="handleRenameLane"
        @delete-lane="handleDeleteLane"
        @move-lane-left="handleMoveLane($event, 'left')"
        @move-lane-right="handleMoveLane($event, 'right')"
        @set-inbox-lane="handleSetInboxLane"
        @rename-card="handleRenameCard"
        @delete-card="handleDeleteCard"
        @archive-card="handleArchiveCard"
        @start-card-drag="handleCardDragStart"
        @end-card-drag="handleCardDragEnd"
        @drop-card="handleDropCard"
      />

      <section class="kanban-board__adder">
        <button
          type="button"
          class="kanban-board__adder-button"
          :disabled="isMutating"
          @click="openAddLaneForm"
        >
          + Add another lane
        </button>
      </section>
    </div>
  </section>
</template>

<style scoped>
.kanban-container {
  flex: 1;
  display: flex;
  flex-direction: column;
  min-height: 0;
  overflow: hidden;
  margin: -24px;
}

.kanban-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 16px;
  padding: 16px 24px 12px;
  border-bottom: 1px solid var(--border);
}

.kanban-header__copy {
  display: flex;
  flex-direction: column;
  gap: 8px;
  min-width: 0;
}

.kanban-header__actions,
.kanban-header__rename-actions {
  display: flex;
  flex-wrap: wrap;
  gap: 8px;
}

.kanban-header__eyebrow {
  margin: 0;
  font-size: 11px;
  font-weight: 600;
  letter-spacing: 0.05em;
  text-transform: uppercase;
  color: var(--muted);
}

.kanban-header__title {
  margin: 0;
  font-size: 28px;
  font-weight: 700;
  line-height: 1.1;
  color: var(--text);
}

.kanban-header__rename-form {
  display: flex;
  flex-direction: column;
  gap: 10px;
}

.kanban-header__rename-input {
  width: min(100%, 360px);
  border: 1px solid var(--border);
  border-radius: var(--radius-btn);
  background: rgba(255, 255, 255, 0.02);
  color: var(--text);
  padding: 10px 12px;
}

.kanban-header__subtitle {
  margin: 6px 0 0;
  font-size: 14px;
  line-height: 1.5;
  color: var(--muted);
}

.kanban-header__button,
.kanban-header__rename-button,
.kanban-state__button,
.kanban-lane-creator__button,
.kanban-banner__button,
.kanban-board__adder-button {
  border: 1px solid var(--border);
  border-radius: var(--radius-btn);
  background: var(--card-bg);
  color: var(--text);
  font-size: 13px;
  font-weight: 600;
  padding: 9px 14px;
  cursor: pointer;
}

.kanban-header__button:disabled,
.kanban-header__rename-button:disabled,
.kanban-state__button:disabled,
.kanban-lane-creator__button:disabled,
.kanban-banner__button:disabled,
.kanban-board__adder-button:disabled {
  cursor: not-allowed;
  opacity: 0.6;
}

.kanban-header__button,
.kanban-header__rename-button--primary,
.kanban-state__button,
.kanban-lane-creator__button--primary,
.kanban-banner__button--primary {
  border-color: var(--accent);
  background: var(--accent);
  color: #fff;
}

.kanban-summary {
  display: grid;
  grid-template-columns: repeat(4, minmax(0, 1fr));
  gap: 12px;
  padding: 12px 24px 0;
}

.kanban-summary__item {
  display: flex;
  flex-direction: column;
  gap: 4px;
  padding: 12px;
  border: 1px solid var(--border);
  border-radius: var(--radius-card);
  background: var(--card-bg);
}

.kanban-summary__value {
  font-size: 20px;
  font-weight: 700;
  color: var(--text);
}

.kanban-summary__label {
  font-size: 11px;
  font-weight: 600;
  letter-spacing: 0.05em;
  text-transform: uppercase;
  color: var(--muted);
}

.kanban-lane-creator {
  padding: 12px 24px 0;
}

.kanban-lane-creator__form {
  display: flex;
  flex-wrap: wrap;
  gap: 10px;
  padding: 12px;
  border: 1px solid var(--border);
  border-radius: var(--radius-card);
  background: var(--card-bg);
}

.kanban-lane-creator__input {
  flex: 1 1 240px;
  min-width: 0;
  border: 1px solid var(--border);
  border-radius: var(--radius-btn);
  background: rgba(255, 255, 255, 0.02);
  color: var(--text);
  padding: 10px 12px;
}

.kanban-lane-creator__actions,
.kanban-banner__actions {
  display: flex;
  flex-wrap: wrap;
  gap: 8px;
}

.kanban-banner {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 16px;
  margin: 12px 24px 0;
  padding: 12px 14px;
  border: 1px solid rgba(248, 113, 113, 0.4);
  border-radius: var(--radius-card);
  background: rgba(127, 29, 29, 0.22);
}

.kanban-toast {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 16px;
  margin: 12px 24px 0;
  padding: 12px 14px;
  border: 1px solid rgba(96, 165, 250, 0.4);
  border-radius: var(--radius-card);
  background: rgba(30, 64, 175, 0.2);
}

.kanban-toast__copy {
  display: flex;
  flex-direction: column;
  gap: 4px;
}

.kanban-toast__title,
.kanban-toast__message {
  margin: 0;
  font-size: 13px;
  line-height: 1.5;
  color: #dbeafe;
}

.kanban-toast__title {
  font-weight: 700;
}

.kanban-toast__button {
  border: 1px solid rgba(191, 219, 254, 0.45);
  border-radius: var(--radius-btn);
  background: transparent;
  color: #dbeafe;
  font-size: 13px;
  font-weight: 600;
  padding: 9px 14px;
  cursor: pointer;
}

.kanban-toast__button:disabled {
  cursor: not-allowed;
  opacity: 0.6;
}

.kanban-banner__copy {
  margin: 0;
  font-size: 13px;
  line-height: 1.5;
  color: #fecaca;
}

.kanban-state {
  display: flex;
  flex: 1;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  gap: 12px;
  padding: 32px 24px;
  text-align: center;
}

.kanban-state__title {
  margin: 0;
  font-size: 20px;
  font-weight: 700;
  color: var(--text);
}

.kanban-state__copy {
  max-width: 460px;
  margin: 0;
  font-size: 14px;
  line-height: 1.6;
  color: var(--muted);
}

.kanban-board {
  display: flex;
  flex: 1;
  gap: 16px;
  min-height: 0;
  padding: 16px 24px;
  overflow-x: auto;
  overflow-y: hidden;
}

.kanban-board__adder {
  display: flex;
  align-self: stretch;
  min-width: 220px;
}

.kanban-board__adder-button {
  width: 100%;
  border-style: dashed;
}

@media (max-width: 960px) {
  .kanban-header {
    flex-direction: column;
    align-items: stretch;
  }

  .kanban-summary {
    grid-template-columns: repeat(2, minmax(0, 1fr));
  }

  .kanban-banner {
    flex-direction: column;
    align-items: stretch;
  }

  .kanban-toast {
    flex-direction: column;
    align-items: stretch;
  }
}
</style>
