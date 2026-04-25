import { flushPromises } from "@vue/test-utils";
import { createPinia, setActivePinia } from "pinia";
import { beforeEach, describe, expect, it, vi } from "vitest";
import type {
  Board,
  BoardCard,
  BoardLane,
  CreateBoardCardRequest,
  CreateBoardLaneRequest,
  MoveBoardCardRequest,
  ReorderBoardLanesRequest,
  UpdateBoardCardRequest,
  UpdateBoardLaneRequest,
} from "@/lib/board-api";

const boardApiMocks = vi.hoisted(() => ({
  archiveBoardCard: vi.fn(),
  createBoard: vi.fn(),
  createBoardCard: vi.fn(),
  createBoardLane: vi.fn(),
  deleteBoard: vi.fn(),
  deleteBoardCard: vi.fn(),
  deleteBoardLane: vi.fn(),
  listBoardCards: vi.fn(),
  listBoardLanes: vi.fn(),
  listBoards: vi.fn(),
  moveBoardCard: vi.fn(),
  reorderBoardLanes: vi.fn(),
  syncBoard: vi.fn(),
  updateBoard: vi.fn(),
  updateBoardCard: vi.fn(),
  updateBoardLane: vi.fn(),
}));

vi.mock("@/lib/board-api", () => ({
  archiveBoardCard: boardApiMocks.archiveBoardCard,
  createBoard: boardApiMocks.createBoard,
  createBoardCard: boardApiMocks.createBoardCard,
  createBoardLane: boardApiMocks.createBoardLane,
  deleteBoard: boardApiMocks.deleteBoard,
  deleteBoardCard: boardApiMocks.deleteBoardCard,
  deleteBoardLane: boardApiMocks.deleteBoardLane,
  listBoardCards: boardApiMocks.listBoardCards,
  listBoardLanes: boardApiMocks.listBoardLanes,
  listBoards: boardApiMocks.listBoards,
  moveBoardCard: boardApiMocks.moveBoardCard,
  reorderBoardLanes: boardApiMocks.reorderBoardLanes,
  syncBoard: boardApiMocks.syncBoard,
  updateBoard: boardApiMocks.updateBoard,
  updateBoardCard: boardApiMocks.updateBoardCard,
  updateBoardLane: boardApiMocks.updateBoardLane,
}));

import { useBoardStore } from "@/stores/board";

interface MockBoardState {
  boards: Board[];
  lanes: BoardLane[];
  cards: BoardCard[];
  failures: {
    moveBoardCard: Error | null;
    reorderBoardLanes: Error | null;
  };
}

const POSITION_GAP = 1_024;

let mockState: MockBoardState;

function createTimestamp(minute: number): string {
  return new Date(Date.UTC(2026, 1, 27, 10, minute, 0)).toISOString();
}

function cloneBoard(board: Board): Board {
  return { ...board };
}

function cloneLane(lane: BoardLane): BoardLane {
  return { ...lane };
}

function cloneCard(card: BoardCard): BoardCard {
  return { ...card };
}

function cloneBoards(boards: readonly Board[]): Board[] {
  return boards.map(cloneBoard);
}

function cloneLanes(lanes: readonly BoardLane[]): BoardLane[] {
  return lanes.map(cloneLane);
}

function cloneCards(cards: readonly BoardCard[]): BoardCard[] {
  return cards.map(cloneCard);
}

function createBoardFixture(overrides: Partial<Board> = {}): Board {
  return {
    id: "board-1",
    name: "Platform Roadmap",
    createdAt: createTimestamp(0),
    updatedAt: createTimestamp(0),
    ...overrides,
  };
}

function createLaneFixture(overrides: Partial<BoardLane> = {}): BoardLane {
  return {
    id: "lane-1",
    boardId: "board-1",
    name: "Backlog",
    position: POSITION_GAP,
    isInbox: false,
    createdAt: createTimestamp(1),
    updatedAt: createTimestamp(1),
    ...overrides,
  };
}

function createCardFixture(overrides: Partial<BoardCard> = {}): BoardCard {
  return {
    id: "card-1",
    boardId: "board-1",
    laneId: "lane-1",
    title: "Plan frontend test coverage",
    sourceType: null,
    sourceKey: "manual-1",
    metadata: "Cover kanban workflows with mocked API responses.",
    position: POSITION_GAP,
    archivedAt: null,
    createdAt: createTimestamp(2),
    updatedAt: createTimestamp(3),
    ...overrides,
  };
}

function createEmptyState(): MockBoardState {
  return {
    boards: [],
    lanes: [],
    cards: [],
    failures: {
      moveBoardCard: null,
      reorderBoardLanes: null,
    },
  };
}

function createLoadedState(): MockBoardState {
  const board = createBoardFixture();

  return {
    boards: [board],
    lanes: [
      createLaneFixture({ id: "lane-backlog", name: "Backlog", isInbox: true, createdAt: createTimestamp(1), updatedAt: createTimestamp(1) }),
      createLaneFixture({ id: "lane-doing", name: "In Progress", position: POSITION_GAP * 2, createdAt: createTimestamp(2), updatedAt: createTimestamp(2) }),
      createLaneFixture({ id: "lane-review", name: "Review", position: POSITION_GAP * 3, createdAt: createTimestamp(3), updatedAt: createTimestamp(3) }),
      createLaneFixture({ id: "lane-done", name: "Done", position: POSITION_GAP * 4, createdAt: createTimestamp(4), updatedAt: createTimestamp(4) }),
    ],
    cards: [
      createCardFixture({ id: "card-backlog", laneId: "lane-backlog", title: "Capture manual QA notes", sourceType: null, sourceKey: "manual-qa", createdAt: createTimestamp(5), updatedAt: createTimestamp(6) }),
      createCardFixture({ id: "card-doing", laneId: "lane-doing", title: "Sync board store with API", sourceType: "github", sourceKey: "issue-42", position: POSITION_GAP, createdAt: createTimestamp(7), updatedAt: createTimestamp(8) }),
      createCardFixture({ id: "card-review", laneId: "lane-review", title: "Review kanban UI contracts", sourceType: "weave", sourceKey: "task-9", position: POSITION_GAP, createdAt: createTimestamp(9), updatedAt: createTimestamp(10) }),
      createCardFixture({ id: "card-done", laneId: "lane-done", title: "Ship persistent board support", sourceType: "github", sourceKey: "pr-77", position: POSITION_GAP, createdAt: createTimestamp(11), updatedAt: createTimestamp(12) }),
    ],
    failures: {
      moveBoardCard: null,
      reorderBoardLanes: null,
    },
  };
}

function normalizeLanePositions(boardId: string): void {
  const orderedLanes = mockState.lanes
    .filter((lane) => lane.boardId === boardId)
    .sort((left, right) => left.position - right.position || left.createdAt.localeCompare(right.createdAt));

  orderedLanes.forEach((lane, index) => {
    lane.position = (index + 1) * POSITION_GAP;
    lane.updatedAt = createTimestamp(20 + index);
  });
}

function normalizeCardPositions(laneId: string): void {
  const orderedCards = mockState.cards
    .filter((card) => card.laneId === laneId && card.archivedAt === null)
    .sort((left, right) => left.position - right.position || left.createdAt.localeCompare(right.createdAt));

  orderedCards.forEach((card, index) => {
    card.position = (index + 1) * POSITION_GAP;
    card.updatedAt = createTimestamp(30 + index);
  });
}

function moveCardInState(cardId: string, request: MoveBoardCardRequest): BoardCard {
  const movingCard = mockState.cards.find((card) => card.id === cardId);
  if (!movingCard) {
    throw new Error("Card not found.");
  }

  const sourceLaneId = movingCard.laneId;
  const targetLaneCards = mockState.cards
    .filter((card) => card.laneId === request.laneId && card.id !== cardId && card.archivedAt === null)
    .sort((left, right) => left.position - right.position || left.createdAt.localeCompare(right.createdAt));
  const boundedIndex = Math.max(0, Math.min(request.position, targetLaneCards.length));

  movingCard.laneId = request.laneId;
  movingCard.position = (boundedIndex + 1) * POSITION_GAP;
  movingCard.updatedAt = createTimestamp(40);

  const reorderedCards = [...targetLaneCards];
  reorderedCards.splice(boundedIndex, 0, movingCard);
  reorderedCards.forEach((card, index) => {
    card.position = (index + 1) * POSITION_GAP;
    card.updatedAt = createTimestamp(40 + index);
  });

  normalizeCardPositions(sourceLaneId);
  normalizeCardPositions(request.laneId);

  return cloneCard(movingCard);
}

function configureBoardApiMocks(): void {
  for (const mock of Object.values(boardApiMocks)) {
    mock.mockReset();
  }

  boardApiMocks.listBoards.mockImplementation(async () => cloneBoards(mockState.boards));
  boardApiMocks.listBoardLanes.mockImplementation(async (boardId: string) => cloneLanes(mockState.lanes.filter((lane) => lane.boardId === boardId)));
  boardApiMocks.listBoardCards.mockImplementation(async (boardId: string) => cloneCards(mockState.cards.filter((card) => card.boardId === boardId)));

  boardApiMocks.createBoard.mockImplementation(async ({ name }: { name: string }) => {
    const board = createBoardFixture({
      id: `board-${mockState.boards.length + 1}`,
      name,
      createdAt: createTimestamp(50),
      updatedAt: createTimestamp(50),
    });

    mockState.boards.push(board);
    return cloneBoard(board);
  });

  boardApiMocks.updateBoard.mockImplementation(async (boardId: string, request: { name: string }) => {
    const board = mockState.boards.find((candidate) => candidate.id === boardId);
    if (!board) {
      throw new Error("Board not found.");
    }

    board.name = request.name;
    board.updatedAt = createTimestamp(51);
    return cloneBoard(board);
  });

  boardApiMocks.deleteBoard.mockImplementation(async (boardId: string) => {
    mockState.boards = mockState.boards.filter((board) => board.id !== boardId);
    mockState.lanes = mockState.lanes.filter((lane) => lane.boardId !== boardId);
    mockState.cards = mockState.cards.filter((card) => card.boardId !== boardId);
  });

  boardApiMocks.createBoardLane.mockImplementation(async (boardId: string, request: CreateBoardLaneRequest) => {
    const boardLanes = mockState.lanes.filter((lane) => lane.boardId === boardId);
    const maxPosition = boardLanes.reduce((current, lane) => Math.max(current, lane.position), 0);
    const lane = createLaneFixture({
      id: `lane-${mockState.lanes.length + 1}`,
      boardId,
      name: request.name,
      position: request.position ?? maxPosition + POSITION_GAP,
      createdAt: createTimestamp(52),
      updatedAt: createTimestamp(52),
    });

    mockState.lanes.push(lane);
    normalizeLanePositions(boardId);
    return cloneLane(lane);
  });

  boardApiMocks.updateBoardLane.mockImplementation(async (boardId: string, laneId: string, request: UpdateBoardLaneRequest) => {
    const lane = mockState.lanes.find((candidate) => candidate.id === laneId && candidate.boardId === boardId);
    if (!lane) {
      throw new Error("Lane not found.");
    }

    if (request.name !== undefined) {
      lane.name = request.name;
    }

    if (request.position !== undefined) {
      lane.position = request.position;
    }

    if (request.isInbox === true) {
      mockState.lanes
        .filter((candidate) => candidate.boardId === boardId)
        .forEach((candidate) => {
          candidate.isInbox = candidate.id === laneId;
        });
    }

    lane.updatedAt = createTimestamp(53);
    normalizeLanePositions(boardId);
    return cloneLane(lane);
  });

  boardApiMocks.deleteBoardLane.mockImplementation(async (boardId: string, laneId: string) => {
    mockState.lanes = mockState.lanes.filter((lane) => !(lane.boardId === boardId && lane.id === laneId));
    mockState.cards = mockState.cards.filter((card) => !(card.boardId === boardId && card.laneId === laneId));
    normalizeLanePositions(boardId);
  });

  boardApiMocks.reorderBoardLanes.mockImplementation(async (boardId: string, request: ReorderBoardLanesRequest) => {
    if (mockState.failures.reorderBoardLanes) {
      throw mockState.failures.reorderBoardLanes;
    }

    request.laneIds.forEach((laneId, index) => {
      const lane = mockState.lanes.find((candidate) => candidate.id === laneId && candidate.boardId === boardId);
      if (lane) {
        lane.position = (index + 1) * POSITION_GAP;
        lane.updatedAt = createTimestamp(54 + index);
      }
    });
  });

  boardApiMocks.createBoardCard.mockImplementation(async (boardId: string, request: CreateBoardCardRequest) => {
    const laneCards = mockState.cards.filter((card) => card.laneId === request.laneId && card.archivedAt === null);
    const card = createCardFixture({
      id: `card-${mockState.cards.length + 1}`,
      boardId,
      laneId: request.laneId,
      title: request.title,
      position: request.position ?? (laneCards.length + 1) * POSITION_GAP,
      createdAt: createTimestamp(60),
      updatedAt: createTimestamp(60),
    });

    mockState.cards.push(card);
    normalizeCardPositions(request.laneId);
    return cloneCard(card);
  });

  boardApiMocks.updateBoardCard.mockImplementation(async (boardId: string, cardId: string, request: UpdateBoardCardRequest) => {
    const card = mockState.cards.find((candidate) => candidate.id === cardId && candidate.boardId === boardId);
    if (!card) {
      throw new Error("Card not found.");
    }

    if (request.title !== undefined) {
      card.title = request.title;
    }

    if (request.laneId !== undefined) {
      card.laneId = request.laneId;
    }

    if (request.position !== undefined) {
      card.position = request.position;
      normalizeCardPositions(card.laneId);
    }

    card.updatedAt = createTimestamp(61);
    return cloneCard(card);
  });

  boardApiMocks.deleteBoardCard.mockImplementation(async (boardId: string, cardId: string) => {
    const deletedCard = mockState.cards.find((card) => card.id === cardId && card.boardId === boardId);
    mockState.cards = mockState.cards.filter((card) => !(card.boardId === boardId && card.id === cardId));

    if (deletedCard) {
      normalizeCardPositions(deletedCard.laneId);
    }
  });

  boardApiMocks.archiveBoardCard.mockImplementation(async (boardId: string, cardId: string) => {
    const card = mockState.cards.find((candidate) => candidate.id === cardId && candidate.boardId === boardId);
    if (!card) {
      throw new Error("Card not found.");
    }

    card.archivedAt = createTimestamp(62);
    card.updatedAt = createTimestamp(62);
    return cloneCard(card);
  });

  boardApiMocks.moveBoardCard.mockImplementation(async (boardId: string, cardId: string, request: MoveBoardCardRequest) => {
    if (mockState.failures.moveBoardCard) {
      throw mockState.failures.moveBoardCard;
    }

    const card = mockState.cards.find((candidate) => candidate.id === cardId && candidate.boardId === boardId);
    if (!card) {
      throw new Error("Card not found.");
    }

    return moveCardInState(cardId, request);
  });

  boardApiMocks.syncBoard.mockImplementation(async (boardId: string) => {
    const board = mockState.boards.find((candidate) => candidate.id === boardId);
    if (!board) {
      throw new Error("Board not found.");
    }

    const syncedCard = createCardFixture({
      id: "card-synced",
      boardId,
      laneId: "lane-backlog",
      title: "Synced issue from GitHub",
      sourceType: "github",
      sourceKey: "github:acme/rocket#88",
      position: POSITION_GAP * 2,
      createdAt: createTimestamp(63),
      updatedAt: createTimestamp(63),
    });

    const existingCard = mockState.cards.find((candidate) => candidate.id === "card-doing");
    if (existingCard) {
      existingCard.title = "Sync board store with API (updated)";
      existingCard.updatedAt = createTimestamp(64);
    }

    mockState.cards.push(syncedCard);
    normalizeCardPositions("lane-backlog");
    normalizeCardPositions("lane-doing");

    return {
      sourcesProcessed: 1,
      issuesFetched: 2,
      cardsCreated: 1,
      cardsUpdated: 1,
      cardsMarkedStale: 0,
      syncedAt: createTimestamp(65),
    };
  });
}

describe("useBoardStore", () => {
  beforeEach(() => {
    setActivePinia(createPinia());
    mockState = createLoadedState();
    configureBoardApiMocks();
  });

  it("loads the persisted board and derives board session stats", async () => {
    const store = useBoardStore();
    await flushPromises();

    expect(boardApiMocks.listBoards).toHaveBeenCalledTimes(1);
    expect(store.board?.name).toBe("Platform Roadmap");
    expect(store.lanesWithCards.map((entry) => [entry.lane.name, entry.cards.length])).toEqual([
      ["Backlog", 1],
      ["In Progress", 1],
      ["Review", 1],
      ["Done", 1],
    ]);
    expect(store.quickStats).toEqual({
      total: 4,
      visible: 4,
      active: 1,
      completed: 1,
      projects: 1,
    });
    expect(store.filterSummary).toBe("4 visible • grouped by status");
    expect(store.availableAgents).toEqual(["github", "manual", "weave"]);
    expect(store.groupedSessions.map((group) => group.key)).toEqual([
      "active",
      "idle",
      "waiting_input",
      "completed",
    ]);
  });

  it("defaults to work mode and updates board mode through store actions", async () => {
    const store = useBoardStore();
    await flushPromises();

    expect(store.boardMode).toBe("work");
    expect(store.isManageMode).toBe(false);

    store.toggleBoardMode();

    expect(store.boardMode).toBe("manage");
    expect(store.isManageMode).toBe(true);

    store.setBoardMode("work");

    expect(store.boardMode).toBe("work");
    expect(store.isManageMode).toBe(false);
  });

  it("creates a board, lane, and card through the mocked board API", async () => {
    mockState = createEmptyState();
    configureBoardApiMocks();

    const store = useBoardStore();
    await flushPromises();

    expect(store.hasBoard).toBe(false);

    const lane = await store.createLane({ name: "Inbox" });

    expect(boardApiMocks.createBoard).toHaveBeenCalledWith({ name: "My Board" });
    expect(boardApiMocks.createBoardLane).toHaveBeenCalledWith("board-1", { name: "Inbox" });
    expect(store.board?.name).toBe("My Board");
    expect(store.sortedLanes.map((entry) => entry.name)).toEqual(["Inbox"]);

    await store.createCard({ laneId: lane.id, title: "Write frontend regression tests" });

    expect(boardApiMocks.createBoardCard).toHaveBeenCalledWith("board-1", {
      laneId: lane.id,
      title: "Write frontend regression tests",
    });
    expect(store.getCardsForLane(lane.id).map((card) => card.title)).toEqual([
      "Write frontend regression tests",
    ]);
  });

  it("optimistically reorders lanes and rolls back when the API rejects", async () => {
    const store = useBoardStore();
    await flushPromises();

    const originalOrder = store.sortedLanes.map((lane) => lane.id);
    mockState.failures.reorderBoardLanes = new Error("Lane reorder failed.");

    const mutation = store.reorderLanes([
      "lane-done",
      "lane-review",
      "lane-doing",
      "lane-backlog",
    ]);

    expect(store.sortedLanes.map((lane) => lane.id)).toEqual([
      "lane-done",
      "lane-review",
      "lane-doing",
      "lane-backlog",
    ]);

    await expect(mutation).rejects.toThrow("Lane reorder failed.");

    expect(store.sortedLanes.map((lane) => lane.id)).toEqual(originalOrder);
    expect(store.error).toBe("Lane reorder failed.");
  });

  it("optimistically moves cards and restores the previous state on failure", async () => {
    const store = useBoardStore();
    await flushPromises();

    const originalLaneId = store.getCardById("card-backlog")?.laneId;
    mockState.failures.moveBoardCard = new Error("Card move failed.");

    const mutation = store.moveCard("card-backlog", {
      laneId: "lane-review",
      position: 1,
    });

    expect(store.getCardById("card-backlog")?.laneId).toBe("lane-review");
    expect(store.getCardsForLane("lane-review").map((card) => card.id)).toEqual([
      "card-review",
      "card-backlog",
    ]);

    await expect(mutation).rejects.toThrow("Card move failed.");

    expect(store.getCardById("card-backlog")?.laneId).toBe(originalLaneId);
    expect(store.getCardsForLane("lane-review").map((card) => card.id)).toEqual(["card-review"]);
    expect(store.error).toBe("Card move failed.");
  });

  it("syncs the board and refreshes lanes and cards from the API", async () => {
    boardApiMocks.syncBoard.mockImplementationOnce(async (boardId: string) => {
      const board = mockState.boards.find((candidate) => candidate.id === boardId);
      if (!board) {
        throw new Error("Board not found.");
      }

      mockState.lanes = mockState.lanes.map((lane) => {
        if (lane.id === "lane-backlog") {
          return {
            ...lane,
            name: "Triage",
            isInbox: false,
            updatedAt: createTimestamp(63),
          };
        }

        if (lane.id === "lane-doing") {
          return {
            ...lane,
            isInbox: true,
            updatedAt: createTimestamp(64),
          };
        }

        return lane;
      });

      const syncedCard = createCardFixture({
        id: "card-synced",
        boardId,
        laneId: "lane-backlog",
        title: "Synced issue from GitHub",
        sourceType: "github",
        sourceKey: "github:acme/rocket#88",
        position: POSITION_GAP * 2,
        createdAt: createTimestamp(65),
        updatedAt: createTimestamp(65),
      });

      const existingCard = mockState.cards.find((candidate) => candidate.id === "card-doing");
      if (existingCard) {
        existingCard.title = "Sync board store with API (updated)";
        existingCard.updatedAt = createTimestamp(66);
      }

      mockState.cards.push(syncedCard);
      normalizeCardPositions("lane-backlog");
      normalizeCardPositions("lane-doing");

      return {
        sourcesProcessed: 1,
        issuesFetched: 2,
        cardsCreated: 1,
        cardsUpdated: 1,
        cardsMarkedStale: 0,
        syncedAt: createTimestamp(67),
      };
    });

    const store = useBoardStore();
    await flushPromises();

    const result = await store.syncBoard();

    expect(boardApiMocks.syncBoard).toHaveBeenCalledWith("board-1");
    expect(boardApiMocks.listBoardLanes).toHaveBeenCalledTimes(2);
    expect(boardApiMocks.listBoardCards).toHaveBeenCalledTimes(2);
    expect(result).toEqual({
      sourcesProcessed: 1,
      issuesFetched: 2,
      cardsCreated: 1,
      cardsUpdated: 1,
      cardsMarkedStale: 0,
      syncedAt: createTimestamp(67),
    });
    expect(store.inboxLane?.id).toBe("lane-doing");
    expect(store.sortedLanes.map((lane) => [lane.id, lane.name, lane.isInbox])).toEqual([
      ["lane-backlog", "Triage", false],
      ["lane-doing", "In Progress", true],
      ["lane-review", "Review", false],
      ["lane-done", "Done", false],
    ]);
    expect(store.getCardsForLane("lane-backlog").map((card) => card.title)).toEqual([
      "Capture manual QA notes",
      "Synced issue from GitHub",
    ]);
    expect(store.getCardById("card-doing")?.title).toBe("Sync board store with API (updated)");
  });

  it("surfaces sync errors without clearing the current board state", async () => {
    const store = useBoardStore();
    await flushPromises();

    boardApiMocks.syncBoard.mockRejectedValueOnce(new Error("Board sync failed."));

    const initialLaneSnapshot = store.sortedLanes.map((lane) => ({ ...lane }));
    const initialBacklogCards = store.getCardsForLane("lane-backlog").map((card) => ({ ...card }));

    await expect(store.syncBoard()).rejects.toThrow("Board sync failed.");

    expect(boardApiMocks.syncBoard).toHaveBeenCalledWith("board-1");
    expect(boardApiMocks.listBoardLanes).toHaveBeenCalledTimes(1);
    expect(boardApiMocks.listBoardCards).toHaveBeenCalledTimes(1);
    expect(store.error).toBe("Board sync failed.");
    expect(store.sortedLanes).toEqual(initialLaneSnapshot);
    expect(store.getCardsForLane("lane-backlog")).toEqual(initialBacklogCards);
    expect(store.getCardById("card-synced")).toBeNull();
  });
});
