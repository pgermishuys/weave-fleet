import { defineStore } from "pinia";
import { computed, reactive, ref, shallowRef, watch } from "vue";
import { resolveAgentColor } from "@/lib/agent-colors";
import {
  archiveBoardCard as archiveBoardCardRequest,
  createBoard as createBoardRequest,
  createBoardCard as createBoardCardRequest,
  createBoardLane as createBoardLaneRequest,
  deleteBoard as deleteBoardRequest,
  deleteBoardCard as deleteBoardCardRequest,
  deleteBoardLane as deleteBoardLaneRequest,
  listBoardCards,
  listBoardLanes,
  listBoards,
  moveBoardCard as moveBoardCardRequest,
  reorderBoardLanes as reorderBoardLanesRequest,
  syncBoard as syncBoardRequest,
  updateBoard as updateBoardRequest,
  updateBoardCard as updateBoardCardRequest,
  updateBoardLane as updateBoardLaneRequest,
} from "@/lib/board-api";
import type {
  Board as ApiBoard,
  BoardCard as ApiBoardCard,
  BoardLane as ApiBoardLane,
  BoardSyncResult,
  CreateBoardCardRequest,
  CreateBoardLaneRequest,
  MoveBoardCardRequest,
  UpdateBoardCardRequest,
  UpdateBoardLaneRequest,
} from "@/lib/board-api";

const POSITION_GAP = 1_024;
const DEFAULT_BOARD_NAME = "My Board";

export type Board = ApiBoard;
export type BoardLane = ApiBoardLane;
export type BoardCard = ApiBoardCard;

export type BoardStatus = "active" | "idle" | "waiting_input" | "completed" | "error";
export type BoardGroupBy = "status" | "project" | "agent";
export type BoardSortBy = "newest" | "oldest" | "title";

export interface BoardSession {
  id: string;
  title: string;
  projectName: string;
  projectColor: string;
  status: BoardStatus;
  agent: string;
  modelName: string;
  createdAt: Date;
  completedAt?: Date;
  prompt: string;
  totalTokens: number;
  cost: number;
  durationSeconds: number;
  progressPercent: number;
  progressLabel: string;
}

export interface BoardGroup {
  key: string;
  label: string;
  sessions: BoardSession[];
}

export interface BoardLaneWithCards {
  lane: BoardLane;
  cards: BoardCard[];
}

export interface BoardStatusOption {
  value: BoardStatus;
  label: string;
  color: string;
}

export interface BoardSortOption {
  value: BoardSortBy;
  label: string;
}

export const boardStatusOptions: readonly BoardStatusOption[] = [
  { value: "active", label: "Active", color: "var(--running)" },
  { value: "idle", label: "Idle", color: "#71717a" },
  { value: "waiting_input", label: "Waiting", color: "#f59e0b" },
  { value: "completed", label: "Complete", color: "var(--complete)" },
  { value: "error", label: "Error", color: "var(--error)" },
] as const;

export const boardSortOptions: readonly BoardSortOption[] = [
  { value: "newest", label: "Newest first" },
  { value: "oldest", label: "Oldest first" },
  { value: "title", label: "Title (A–Z)" },
] as const;

const groupByLabels: Record<BoardGroupBy, string> = {
  status: "Status",
  project: "Project",
  agent: "Agent",
};

function toErrorMessage(error: unknown, fallbackMessage: string): string {
  if (error instanceof Error && error.message.trim().length > 0) {
    return error.message;
  }

  return fallbackMessage;
}

function sortBoardLanes(values: readonly BoardLane[]): BoardLane[] {
  return [...values].sort((left, right) => {
    if (left.position !== right.position) {
      return left.position - right.position;
    }

    return left.createdAt.localeCompare(right.createdAt);
  });
}

function sortBoardCards(values: readonly BoardCard[]): BoardCard[] {
  return [...values].sort((left, right) => {
    if (left.laneId !== right.laneId) {
      return left.laneId.localeCompare(right.laneId);
    }

    if (left.position !== right.position) {
      return left.position - right.position;
    }

    return left.createdAt.localeCompare(right.createdAt);
  });
}

function cloneLanes(values: readonly BoardLane[]): BoardLane[] {
  return values.map((lane) => ({ ...lane }));
}

function cloneCards(values: readonly BoardCard[]): BoardCard[] {
  return values.map((card) => ({ ...card }));
}

function patchLane(values: readonly BoardLane[], nextLane: BoardLane): BoardLane[] {
  const nextLanes = cloneLanes(values);
  const laneIndex = nextLanes.findIndex((lane) => lane.id === nextLane.id);
  if (laneIndex >= 0) {
    nextLanes.splice(laneIndex, 1, { ...nextLane });
  } else {
    nextLanes.push({ ...nextLane });
  }

  return sortBoardLanes(nextLanes);
}

function patchCard(values: readonly BoardCard[], nextCard: BoardCard): BoardCard[] {
  const nextCards = cloneCards(values);
  const cardIndex = nextCards.findIndex((card) => card.id === nextCard.id);
  if (cardIndex >= 0) {
    nextCards.splice(cardIndex, 1, { ...nextCard });
  } else {
    nextCards.push({ ...nextCard });
  }

  return sortBoardCards(nextCards);
}

function reorderLaneCollection(values: readonly BoardLane[], laneIds: readonly string[]): BoardLane[] {
  const lanesById = new Map(cloneLanes(values).map((lane) => [lane.id, lane]));
  const orderedLanes = laneIds
    .map((laneId) => lanesById.get(laneId))
    .filter((lane): lane is BoardLane => lane !== undefined);
  const remainingLanes = sortBoardLanes(values).filter((lane) => !laneIds.includes(lane.id));

  return [...orderedLanes, ...remainingLanes].map((lane, index) => ({
    ...lane,
    position: (index + 1) * POSITION_GAP,
  }));
}

function moveCardCollection(
  values: readonly BoardCard[],
  cardId: string,
  targetLaneId: string,
  targetIndex: number,
): BoardCard[] {
  const nextCards = cloneCards(values);
  const movingCard = nextCards.find((card) => card.id === cardId);
  if (!movingCard) {
    return nextCards;
  }

  const sourceLaneId = movingCard.laneId;
  const laneCards = new Map<string, BoardCard[]>();

  for (const card of nextCards) {
    if (card.archivedAt !== null || card.id === cardId) {
      continue;
    }

    const cards = laneCards.get(card.laneId) ?? [];
    cards.push(card);
    laneCards.set(card.laneId, cards);
  }

  const targetCards = laneCards.get(targetLaneId) ?? [];
  const boundedIndex = Math.max(0, Math.min(targetIndex, targetCards.length));

  movingCard.laneId = targetLaneId;
  targetCards.splice(boundedIndex, 0, movingCard);
  laneCards.set(targetLaneId, targetCards);

  for (const laneId of new Set([sourceLaneId, targetLaneId])) {
    const cards = laneCards.get(laneId) ?? [];
    cards.forEach((card, index) => {
      card.position = (index + 1) * POSITION_GAP;
    });
  }

  return sortBoardCards(nextCards);
}

function inferLegacyStatus(card: BoardCard, lane?: BoardLane): BoardStatus {
  if (card.archivedAt !== null) {
    return "completed";
  }

  const laneName = lane?.name.toLowerCase() ?? "";

  if (lane?.isInbox || /(inbox|todo|backlog|queued)/.test(laneName)) {
    return "idle";
  }

  if (/(review|approval|approve|qa)/.test(laneName)) {
    return "waiting_input";
  }

  if (/(done|complete|completed|shipped)/.test(laneName)) {
    return "completed";
  }

  if (/(error|failed|blocked)/.test(laneName)) {
    return "error";
  }

  return "active";
}

function getProgress(status: BoardStatus): { progressLabel: string; progressPercent: number } {
  switch (status) {
    case "idle":
      return {
        progressLabel: "Queued",
        progressPercent: 12,
      };
    case "waiting_input":
      return {
        progressLabel: "Awaiting review",
        progressPercent: 82,
      };
    case "completed":
      return {
        progressLabel: "Complete",
        progressPercent: 100,
      };
    case "error":
      return {
        progressLabel: "Failed",
        progressPercent: 100,
      };
    case "active":
    default:
      return {
        progressLabel: "In progress",
        progressPercent: 56,
      };
  }
}

function toLegacySession(card: BoardCard, board: Board | null, lane?: BoardLane): BoardSession {
  const status = inferLegacyStatus(card, lane);
  const progress = getProgress(status);
  const createdAt = new Date(card.createdAt);
  const updatedAt = new Date(card.updatedAt);
  const completedAt = card.archivedAt === null ? undefined : new Date(card.archivedAt);
  const projectName = board?.name ?? "Board";
  const agent = card.sourceType ?? "manual";

  return {
    id: card.id,
    title: card.title,
    projectName,
    projectColor: resolveAgentColor(projectName),
    status,
    agent,
    modelName: card.sourceKey ?? (card.sourceType === null ? "Manual" : `${card.sourceType} source`),
    createdAt,
    completedAt,
    prompt: card.metadata ?? card.title,
    totalTokens: 0,
    cost: 0,
    durationSeconds: Math.max(Math.round((updatedAt.getTime() - createdAt.getTime()) / 1000), 60),
    progressPercent: progress.progressPercent,
    progressLabel: progress.progressLabel,
  };
}

export function getBoardStatusMeta(status: BoardStatus): BoardStatusOption {
  return boardStatusOptions.find((option) => option.value === status) ?? boardStatusOptions[0];
}

export const useBoardStore = defineStore("board", () => {
  const board = shallowRef<Board | null>(null);
  const lanes = ref<BoardLane[]>([]);
  const cards = ref<BoardCard[]>([]);
  const isLoading = shallowRef(false);
  const isLoaded = shallowRef(false);
  const error = shallowRef<string | null>(null);
  const pendingMutations = shallowRef(0);

  const selectedProject = shallowRef("all");
  const groupBy = shallowRef<BoardGroupBy>("status");
  const sortBy = shallowRef<BoardSortBy>("newest");
  const statusFilters = reactive<Record<BoardStatus, boolean>>({
    active: true,
    idle: true,
    waiting_input: true,
    completed: true,
    error: true,
  });
  const agentFilters = reactive<Record<string, boolean>>({});

  let loadPromise: Promise<void> | null = null;

  const boardId = computed(() => board.value?.id ?? null);
  const hasBoard = computed(() => board.value !== null);
  const isMutating = computed(() => pendingMutations.value > 0);
  const sortedLanes = computed<BoardLane[]>(() => sortBoardLanes(lanes.value));
  const activeCards = computed<BoardCard[]>(() => cards.value.filter((card) => card.archivedAt === null));
  const archivedCards = computed<BoardCard[]>(() => cards.value.filter((card) => card.archivedAt !== null));
  const inboxLane = computed<BoardLane | null>(() => sortedLanes.value.find((lane) => lane.isInbox) ?? null);
  const laneMap = computed(() => new Map(sortedLanes.value.map((lane) => [lane.id, lane])));
  const cardMap = computed(() => new Map(cards.value.map((card) => [card.id, card])));
  const cardsByLaneId = computed(() => {
    const groupedCards = new Map<string, BoardCard[]>();

    for (const lane of sortedLanes.value) {
      groupedCards.set(lane.id, []);
    }

    for (const card of activeCards.value) {
      const laneCards = groupedCards.get(card.laneId) ?? [];
      laneCards.push(card);
      groupedCards.set(card.laneId, laneCards);
    }

    for (const laneCards of groupedCards.values()) {
      laneCards.sort((left, right) => left.position - right.position || left.createdAt.localeCompare(right.createdAt));
    }

    return groupedCards;
  });
  const lanesWithCards = computed<BoardLaneWithCards[]>(() => {
    return sortedLanes.value.map((lane) => ({
      lane,
      cards: [...(cardsByLaneId.value.get(lane.id) ?? [])],
    }));
  });
  const isEmpty = computed(() => hasBoard.value && sortedLanes.value.length === 0 && activeCards.value.length === 0);

  const sessions = computed<BoardSession[]>(() => {
    return sortBoardCards(cards.value).map((card) => toLegacySession(card, board.value, laneMap.value.get(card.laneId)));
  });

  const availableProjects = computed<string[]>(() => {
    return board.value === null ? [] : [board.value.name];
  });

  const availableAgents = computed<string[]>(() => {
    return [...new Set(sessions.value.map((session) => session.agent))].sort((left, right) => left.localeCompare(right));
  });

  const filteredSessions = computed<BoardSession[]>(() => {
    return sessions.value.filter((session) => {
      const projectMatches = selectedProject.value === "all" || session.projectName === selectedProject.value;
      const statusMatches = statusFilters[session.status] ?? false;
      const agentMatches = agentFilters[session.agent] ?? true;

      return projectMatches && statusMatches && agentMatches;
    });
  });

  const sortedSessions = computed<BoardSession[]>(() => {
    return [...filteredSessions.value].sort((left, right) => {
      switch (sortBy.value) {
        case "oldest":
          return left.createdAt.getTime() - right.createdAt.getTime();
        case "title":
          return left.title.localeCompare(right.title);
        case "newest":
        default:
          return right.createdAt.getTime() - left.createdAt.getTime();
      }
    });
  });

  const groupedSessions = computed<BoardGroup[]>(() => {
    const groups = new Map<string, BoardSession[]>();

    for (const session of sortedSessions.value) {
      const key = groupBy.value === "status"
        ? session.status
        : groupBy.value === "project"
          ? session.projectName
          : session.agent;
      const groupSessions = groups.get(key) ?? [];

      groupSessions.push(session);
      groups.set(key, groupSessions);
    }

    const grouped = [...groups.entries()].map(([key, groupSessions]) => ({
      key,
      label: groupBy.value === "status" ? getBoardStatusMeta(key as BoardStatus).label : key,
      sessions: groupSessions,
    }));

    if (groupBy.value === "status") {
      return grouped.sort((left, right) => {
        const leftIndex = boardStatusOptions.findIndex((option) => option.value === left.key);
        const rightIndex = boardStatusOptions.findIndex((option) => option.value === right.key);

        return leftIndex - rightIndex;
      });
    }

    return grouped.sort((left, right) => left.label.localeCompare(right.label));
  });

  const quickStats = computed(() => {
    const visibleSessions = filteredSessions.value;

    return {
      total: sessions.value.length,
      visible: visibleSessions.length,
      active: visibleSessions.filter((session) => session.status === "active").length,
      completed: visibleSessions.filter((session) => session.status === "completed").length,
      projects: new Set(visibleSessions.map((session) => session.projectName)).size,
    };
  });

  const filterSummary = computed(() => {
    return `${quickStats.value.visible} visible • grouped by ${groupByLabels[groupBy.value].toLowerCase()}`;
  });

  watch(
    availableAgents,
    (agents) => {
      const nextAgents = new Set(agents);

      for (const agent of Object.keys(agentFilters)) {
        if (!nextAgents.has(agent)) {
          delete agentFilters[agent];
        }
      }

      for (const agent of agents) {
        if (!(agent in agentFilters)) {
          agentFilters[agent] = true;
        }
      }
    },
    { immediate: true },
  );

  watch(
    availableProjects,
    (projects) => {
      if (selectedProject.value !== "all" && !projects.includes(selectedProject.value)) {
        selectedProject.value = "all";
      }
    },
    { immediate: true },
  );

  function requireBoardId(): string {
    if (board.value === null) {
      throw new Error("Board is not loaded.");
    }

    return board.value.id;
  }

  async function refreshLanes(): Promise<void> {
    const nextBoardId = requireBoardId();
    lanes.value = sortBoardLanes(await listBoardLanes(nextBoardId));
  }

  async function refreshCards(): Promise<void> {
    const nextBoardId = requireBoardId();
    cards.value = sortBoardCards(await listBoardCards(nextBoardId));
  }

  async function runMutation<T>(mutation: () => Promise<T>): Promise<T> {
    pendingMutations.value += 1;
    error.value = null;

    try {
      return await mutation();
    } catch (nextError) {
      const message = toErrorMessage(nextError, "Board request failed.");
      error.value = message;
      throw nextError instanceof Error ? nextError : new Error(message);
    } finally {
      pendingMutations.value -= 1;
    }
  }

  async function loadBoard(): Promise<void> {
    if (loadPromise !== null) {
      return loadPromise;
    }

    const nextLoadPromise = (async () => {
      isLoading.value = true;
      error.value = null;

      try {
        const boards = await listBoards();
        const nextBoard = board.value === null
          ? boards[0] ?? null
          : boards.find((candidate) => candidate.id === board.value?.id) ?? boards[0] ?? null;

        if (nextBoard === null) {
          board.value = null;
          lanes.value = [];
          cards.value = [];
          isLoaded.value = true;
          return;
        }

        const [nextLanes, nextCards] = await Promise.all([
          listBoardLanes(nextBoard.id),
          listBoardCards(nextBoard.id),
        ]);

        board.value = nextBoard;
        lanes.value = sortBoardLanes(nextLanes);
        cards.value = sortBoardCards(nextCards);
        isLoaded.value = true;
      } catch (nextError) {
        const message = toErrorMessage(nextError, "Failed to load board.");
        error.value = message;
        throw nextError instanceof Error ? nextError : new Error(message);
      } finally {
        isLoading.value = false;
        loadPromise = null;
      }
    })();

    loadPromise = nextLoadPromise;
    return nextLoadPromise;
  }

  async function reloadBoard(): Promise<void> {
    loadPromise = null;
    await loadBoard();
  }

  async function syncBoard(): Promise<BoardSyncResult> {
    return runMutation(async () => {
      const nextBoardId = requireBoardId();
      const syncResult = await syncBoardRequest(nextBoardId);
      await Promise.all([refreshLanes(), refreshCards()]);
      return syncResult;
    });
  }

  function clearError(): void {
    error.value = null;
  }

  function getLaneById(laneId: string): BoardLane | null {
    return laneMap.value.get(laneId) ?? null;
  }

  function getCardById(cardId: string): BoardCard | null {
    return cardMap.value.get(cardId) ?? null;
  }

  function getCardsForLane(laneId: string): BoardCard[] {
    return [...(cardsByLaneId.value.get(laneId) ?? [])];
  }

  async function createBoard(name: string): Promise<Board> {
    return runMutation(async () => {
      const nextBoard = await createBoardRequest({ name });
      board.value = nextBoard;
      lanes.value = [];
      cards.value = [];
      isLoaded.value = true;
      return nextBoard;
    });
  }

  async function ensureBoard(name = DEFAULT_BOARD_NAME): Promise<Board> {
    if (board.value !== null) {
      return board.value;
    }

    await loadBoard();
    if (board.value !== null) {
      return board.value;
    }

    return createBoard(name);
  }

  async function renameBoard(name: string): Promise<Board> {
    return runMutation(async () => {
      const nextBoardId = requireBoardId();
      const updatedBoard = await updateBoardRequest(nextBoardId, { name });
      board.value = updatedBoard;
      return updatedBoard;
    });
  }

  async function removeBoard(): Promise<void> {
    await runMutation(async () => {
      const nextBoardId = requireBoardId();
      await deleteBoardRequest(nextBoardId);
      board.value = null;
      lanes.value = [];
      cards.value = [];
      isLoaded.value = true;
    });
  }

  async function createLane(request: CreateBoardLaneRequest): Promise<BoardLane> {
    return runMutation(async () => {
      const nextBoardId = (await ensureBoard()).id;
      const createdLane = await createBoardLaneRequest(nextBoardId, request);
      await refreshLanes();
      return getLaneById(createdLane.id) ?? createdLane;
    });
  }

  async function updateLane(laneId: string, request: UpdateBoardLaneRequest): Promise<BoardLane> {
    return runMutation(async () => {
      const nextBoardId = requireBoardId();
      const updatedLane = await updateBoardLaneRequest(nextBoardId, laneId, request);

      if (request.position !== undefined || request.isInbox !== undefined) {
        await refreshLanes();
        return getLaneById(updatedLane.id) ?? updatedLane;
      }

      lanes.value = patchLane(lanes.value, updatedLane);
      return updatedLane;
    });
  }

  async function renameLane(laneId: string, name: string): Promise<BoardLane> {
    return updateLane(laneId, { name });
  }

  async function setInboxLane(laneId: string): Promise<BoardLane> {
    return updateLane(laneId, { isInbox: true });
  }

  async function deleteLane(laneId: string): Promise<void> {
    await runMutation(async () => {
      const nextBoardId = requireBoardId();
      await deleteBoardLaneRequest(nextBoardId, laneId);
      lanes.value = lanes.value.filter((lane) => lane.id !== laneId);
    });
  }

  async function reorderLanes(laneIds: readonly string[]): Promise<void> {
    const previousLanes = cloneLanes(lanes.value);

    lanes.value = reorderLaneCollection(lanes.value, laneIds);

    try {
      await runMutation(async () => {
        const nextBoardId = requireBoardId();
        await reorderBoardLanesRequest(nextBoardId, { laneIds });
        await refreshLanes();
      });
    } catch (nextError) {
      lanes.value = previousLanes;
      throw nextError;
    }
  }

  async function createCard(request: CreateBoardCardRequest): Promise<BoardCard> {
    return runMutation(async () => {
      const nextBoardId = (await ensureBoard()).id;
      const createdCard = await createBoardCardRequest(nextBoardId, request);
      await refreshCards();
      return getCardById(createdCard.id) ?? createdCard;
    });
  }

  async function updateCard(cardId: string, request: UpdateBoardCardRequest): Promise<BoardCard> {
    return runMutation(async () => {
      const nextBoardId = requireBoardId();
      const updatedCard = await updateBoardCardRequest(nextBoardId, cardId, request);

      if (request.laneId !== undefined || request.position !== undefined) {
        await refreshCards();
        return getCardById(updatedCard.id) ?? updatedCard;
      }

      cards.value = patchCard(cards.value, updatedCard);
      return updatedCard;
    });
  }

  async function renameCard(cardId: string, title: string): Promise<BoardCard> {
    return updateCard(cardId, { title });
  }

  async function deleteCard(cardId: string): Promise<void> {
    await runMutation(async () => {
      const nextBoardId = requireBoardId();
      await deleteBoardCardRequest(nextBoardId, cardId);
      cards.value = cards.value.filter((card) => card.id !== cardId);
    });
  }

  async function archiveCard(cardId: string): Promise<BoardCard> {
    return runMutation(async () => {
      const nextBoardId = requireBoardId();
      const archivedCard = await archiveBoardCardRequest(nextBoardId, cardId);
      cards.value = patchCard(cards.value, archivedCard);
      return archivedCard;
    });
  }

  async function moveCard(cardId: string, request: MoveBoardCardRequest): Promise<BoardCard> {
    const previousCards = cloneCards(cards.value);

    cards.value = moveCardCollection(cards.value, cardId, request.laneId, request.position);

    try {
      return await runMutation(async () => {
        const nextBoardId = requireBoardId();
        const movedCard = await moveBoardCardRequest(nextBoardId, cardId, request);
        await refreshCards();
        return getCardById(movedCard.id) ?? movedCard;
      });
    } catch (nextError) {
      cards.value = previousCards;
      throw nextError;
    }
  }

  async function reorderCard(cardId: string, position: number): Promise<BoardCard> {
    const card = getCardById(cardId);
    if (card === null) {
      throw new Error("Card not found.");
    }

    return moveCard(cardId, {
      laneId: card.laneId,
      position,
    });
  }

  function setSelectedProject(project: string): void {
    selectedProject.value = project;
  }

  function setStatusFilter(status: BoardStatus, enabled: boolean): void {
    statusFilters[status] = enabled;
  }

  function setAgentFilter(agent: string, enabled: boolean): void {
    agentFilters[agent] = enabled;
  }

  function setGroupBy(value: BoardGroupBy): void {
    groupBy.value = value;
  }

  function setSortBy(value: BoardSortBy): void {
    sortBy.value = value;
  }

  void loadBoard().catch(() => {
    // initial load errors are exposed via store state
  });

  return {
    board,
    boardId,
    hasBoard,
    lanes,
    sortedLanes,
    cards,
    activeCards,
    archivedCards,
    inboxLane,
    laneMap,
    cardMap,
    cardsByLaneId,
    lanesWithCards,
    isEmpty,
    isLoading,
    isLoaded,
    isMutating,
    error,
    pendingMutations,
    loadBoard,
    reloadBoard,
    syncBoard,
    clearError,
    getLaneById,
    getCardById,
    getCardsForLane,
    createBoard,
    ensureBoard,
    renameBoard,
    removeBoard,
    createLane,
    updateLane,
    renameLane,
    setInboxLane,
    deleteLane,
    reorderLanes,
    createCard,
    updateCard,
    renameCard,
    deleteCard,
    archiveCard,
    moveCard,
    reorderCard,
    sessions,
    selectedProject,
    statusFilters,
    agentFilters,
    groupBy,
    sortBy,
    availableProjects,
    availableAgents,
    filteredSessions,
    sortedSessions,
    groupedSessions,
    quickStats,
    filterSummary,
    setSelectedProject,
    setStatusFilter,
    setAgentFilter,
    setGroupBy,
    setSortBy,
  };
});
