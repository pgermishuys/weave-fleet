import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
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
  createBoardSource: vi.fn(),
  deleteBoard: vi.fn(),
  deleteBoardCard: vi.fn(),
  deleteBoardLane: vi.fn(),
  deleteBoardSource: vi.fn(),
  listBoardCards: vi.fn(),
  listBoardLanes: vi.fn(),
  listBoards: vi.fn(),
  listBoardSources: vi.fn(),
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
  createBoardSource: boardApiMocks.createBoardSource,
  deleteBoard: boardApiMocks.deleteBoard,
  deleteBoardCard: boardApiMocks.deleteBoardCard,
  deleteBoardLane: boardApiMocks.deleteBoardLane,
  deleteBoardSource: boardApiMocks.deleteBoardSource,
  listBoardCards: boardApiMocks.listBoardCards,
  listBoardLanes: boardApiMocks.listBoardLanes,
  listBoards: boardApiMocks.listBoards,
  listBoardSources: boardApiMocks.listBoardSources,
  moveBoardCard: boardApiMocks.moveBoardCard,
  reorderBoardLanes: boardApiMocks.reorderBoardLanes,
  syncBoard: boardApiMocks.syncBoard,
  updateBoard: boardApiMocks.updateBoard,
  updateBoardCard: boardApiMocks.updateBoardCard,
  updateBoardLane: boardApiMocks.updateBoardLane,
}));

import KanbanBoard from "@/components/board/KanbanBoard.vue";

interface MockBoardState {
  boards: Board[];
  lanes: BoardLane[];
  cards: BoardCard[];
}

const POSITION_GAP = 1_024;

let mockState: MockBoardState;

function createTimestamp(minute: number): string {
  return new Date(Date.UTC(2026, 1, 27, 12, minute, 0)).toISOString();
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

function createBoardFixture(overrides: Partial<Board> = {}): Board {
  return {
    id: "board-1",
    name: "Delivery Board",
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
    title: "Existing card",
    sourceType: null,
    sourceKey: "manual-1",
    metadata: null,
    position: POSITION_GAP,
    archivedAt: null,
    createdAt: createTimestamp(2),
    updatedAt: createTimestamp(2),
    ...overrides,
  };
}

function configureBoardApiMocks(): void {
  for (const mock of Object.values(boardApiMocks)) {
    mock.mockReset();
  }

  boardApiMocks.listBoards.mockImplementation(async () => mockState.boards.map(cloneBoard));
  boardApiMocks.listBoardLanes.mockImplementation(async (boardId: string) => mockState.lanes.filter((lane) => lane.boardId === boardId).map(cloneLane));
  boardApiMocks.listBoardCards.mockImplementation(async (boardId: string) => mockState.cards.filter((card) => card.boardId === boardId).map(cloneCard));

  boardApiMocks.createBoard.mockImplementation(async ({ name }: { name: string }) => {
    const board = createBoardFixture({
      id: `board-${mockState.boards.length + 1}`,
      name,
      createdAt: createTimestamp(10),
      updatedAt: createTimestamp(10),
    });

    mockState.boards.push(board);
    return cloneBoard(board);
  });

  boardApiMocks.createBoardLane.mockImplementation(async (boardId: string, request: CreateBoardLaneRequest) => {
    const lane = createLaneFixture({
      id: `lane-${mockState.lanes.length + 1}`,
      boardId,
      name: request.name,
      position: request.position ?? (mockState.lanes.length + 1) * POSITION_GAP,
      createdAt: createTimestamp(11),
      updatedAt: createTimestamp(11),
    });

    mockState.lanes.push(lane);
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

    if (request.isInbox === true) {
      mockState.lanes
        .filter((candidate) => candidate.boardId === boardId)
        .forEach((candidate) => {
          candidate.isInbox = candidate.id === laneId;
        });
    }

    lane.updatedAt = createTimestamp(12);
    return cloneLane(lane);
  });

  boardApiMocks.createBoardCard.mockImplementation(async (boardId: string, request: CreateBoardCardRequest) => {
    const card = createCardFixture({
      id: `card-${mockState.cards.length + 1}`,
      boardId,
      laneId: request.laneId,
      title: request.title,
      position: request.position ?? (mockState.cards.filter((candidate) => candidate.laneId === request.laneId).length + 1) * POSITION_GAP,
      createdAt: createTimestamp(13),
      updatedAt: createTimestamp(13),
    });

    mockState.cards.push(card);
    return cloneCard(card);
  });

  boardApiMocks.updateBoard.mockResolvedValue(createBoardFixture());
  boardApiMocks.deleteBoard.mockResolvedValue(undefined);
  boardApiMocks.deleteBoardLane.mockResolvedValue(undefined);
  boardApiMocks.listBoardSources.mockResolvedValue([]);
  boardApiMocks.createBoardSource.mockResolvedValue(undefined);
  boardApiMocks.deleteBoardSource.mockResolvedValue(undefined);
  boardApiMocks.reorderBoardLanes.mockImplementation(async (_boardId: string, _request: ReorderBoardLanesRequest) => undefined);
  boardApiMocks.updateBoardCard.mockImplementation(async (_boardId: string, _cardId: string, _request: UpdateBoardCardRequest) => cloneCard(mockState.cards[0] ?? createCardFixture()));
  boardApiMocks.deleteBoardCard.mockResolvedValue(undefined);
  boardApiMocks.archiveBoardCard.mockImplementation(async (_boardId: string, _cardId: string) => cloneCard(mockState.cards[0] ?? createCardFixture()));
  boardApiMocks.moveBoardCard.mockImplementation(async (_boardId: string, _cardId: string, _request: MoveBoardCardRequest) => cloneCard(mockState.cards[0] ?? createCardFixture()));
  boardApiMocks.syncBoard.mockImplementation(async (boardId: string) => {
    const syncedCard = createCardFixture({
      id: "card-synced",
      boardId,
      laneId: "lane-backlog",
      title: "Synced issue card",
      sourceType: "github",
      sourceKey: "github:acme/rocket#7",
      position: POSITION_GAP * 2,
      createdAt: createTimestamp(14),
      updatedAt: createTimestamp(14),
    });

    const existingCard = mockState.cards.find((candidate) => candidate.id === "card-1");
    if (existingCard) {
      existingCard.title = "Existing card updated";
      existingCard.updatedAt = createTimestamp(15);
    }

    mockState.cards = [
      ...mockState.cards.filter((candidate) => candidate.id !== syncedCard.id),
      syncedCard,
    ];

    return {
      sourcesProcessed: 1,
      issuesFetched: 2,
      cardsCreated: 1,
      cardsUpdated: 1,
      cardsMarkedStale: 3,
      syncedAt: createTimestamp(16),
    };
  });
}

function mountBoard(): VueWrapper {
  return mount(KanbanBoard);
}

function getButtonByText(wrapper: VueWrapper, label: string) {
  const button = wrapper.findAll("button").find((candidate) => candidate.text() === label);
  if (!button) {
    throw new Error(`Unable to find button with label: ${label}`);
  }

  return button;
}

describe("KanbanBoard", () => {
  beforeEach(() => {
    mockState = {
      boards: [createBoardFixture()],
      lanes: [createLaneFixture({ id: "lane-backlog", name: "Backlog", isInbox: true })],
      cards: [],
    };
    configureBoardApiMocks();
  });

  it("creates a card from the lane composer with mocked API responses", async () => {
    const wrapper = mountBoard();
    await flushPromises();

    await getButtonByText(wrapper, "+ Add a card").trigger("click");
    await wrapper.get(".kanban-col__composer-input").setValue("Write board store tests");
    await wrapper.get(".kanban-col__composer-form").trigger("submit");
    await flushPromises();

    expect(boardApiMocks.createBoardCard).toHaveBeenCalledWith("board-1", {
      laneId: "lane-backlog",
      title: "Write board store tests",
    });
    expect(wrapper.text()).toContain("Write board store tests");
    expect(wrapper.get(".kanban-col__count").text()).toBe("1 card");
  });

  it("creates, renames, and assigns an inbox lane through the board UI", async () => {
    mockState = {
      boards: [],
      lanes: [],
      cards: [],
    };
    configureBoardApiMocks();

    const wrapper = mountBoard();
    await flushPromises();

    await getButtonByText(wrapper, "Add Lane").trigger("click");
    await wrapper.get(".kanban-lane-creator__input").setValue("Ready");
    await wrapper.get(".kanban-lane-creator__form").trigger("submit");
    await flushPromises();

    expect(boardApiMocks.createBoard).toHaveBeenCalledWith({ name: "My Board" });
    expect(boardApiMocks.createBoardLane).toHaveBeenCalledWith("board-1", { name: "Ready" });
    expect(wrapper.text()).toContain("Ready");

    const createdLaneId = mockState.lanes[0]?.id;
    expect(createdLaneId).toBeTruthy();

    await getButtonByText(wrapper, "Rename").trigger("click");
    await wrapper.get(".kanban-col__rename-input").setValue("In Review");
    await wrapper.get(".kanban-col__rename-form").trigger("submit");
    await flushPromises();

    expect(boardApiMocks.updateBoardLane).toHaveBeenCalledWith("board-1", createdLaneId, { name: "In Review" });
    expect(wrapper.text()).toContain("In Review");

    await getButtonByText(wrapper, "Make inbox").trigger("click");
    await flushPromises();

    expect(boardApiMocks.updateBoardLane).toHaveBeenCalledWith("board-1", createdLaneId, { isInbox: true });
    expect(getButtonByText(wrapper, "Inbox").attributes("disabled")).toBeDefined();
  });

  it("syncs the board, shows feedback, and refreshes cards", async () => {
    mockState = {
      boards: [createBoardFixture()],
      lanes: [createLaneFixture({ id: "lane-backlog", name: "Backlog", isInbox: true })],
      cards: [createCardFixture({ laneId: "lane-backlog" })],
    };
    configureBoardApiMocks();

    const wrapper = mountBoard();
    await flushPromises();

    await wrapper.get('[data-testid="kanban-sync-button"]').trigger("click");

    expect(wrapper.get('[data-testid="kanban-sync-button"]').text()).toBe("Syncing…");

    await flushPromises();

    expect(boardApiMocks.syncBoard).toHaveBeenCalledWith("board-1");
    expect(wrapper.get('[data-testid="kanban-sync-feedback"]').text()).toContain("Board synced");
    expect(wrapper.get('[data-testid="kanban-sync-feedback"]').text()).toContain("1 added, 1 updated, 3 stale");
    expect(wrapper.text()).toContain("Existing card updated");
    expect(wrapper.text()).toContain("Synced issue card");
    expect(wrapper.get('[data-testid="kanban-sync-button"]').text()).toBe("Sync now");
  });
});
