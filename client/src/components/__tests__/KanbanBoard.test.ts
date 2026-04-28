import { flushPromises, mount, type VueWrapper } from "@vue/test-utils";
import { beforeEach, describe, expect, it, vi } from "vitest";
import type {
  Board,
  BoardCard,
  BoardLane,
  CreateBoardCardRequest,
  CreateBoardLaneRequest,
  MoveBoardCardRequest,
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

const bookmarkState = vi.hoisted(() => ({
  bookmarks: null as unknown as { value: Array<{ fullName: string; owner: string; name: string }> },
  error: null as unknown as { value: string | null },
  isLoading: null as unknown as { value: boolean },
  refresh: vi.fn(async () => undefined),
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

vi.mock("@/plugins/builtin/github/composables/use-github-bookmarks", async () => {
  const { readonly, shallowRef } = await import("vue");

  bookmarkState.bookmarks = shallowRef([
    {
      fullName: "acme/rocket",
      owner: "acme",
      name: "rocket",
    },
  ]);
  bookmarkState.error = shallowRef<string | null>(null);
  bookmarkState.isLoading = shallowRef(false);

  return {
    useGitHubBookmarks: () => ({
      bookmarks: readonly(bookmarkState.bookmarks),
      error: readonly(bookmarkState.error),
      isLoading: readonly(bookmarkState.isLoading),
      refresh: bookmarkState.refresh,
    }),
  };
});

import KanbanBoard from "@/components/board/KanbanBoard.vue";
import { useBoardStore } from "@/stores/board";
import { useSidebarStore } from "@/stores/sidebar";
import { nextTick } from "vue";

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
  boardApiMocks.reorderBoardLanes.mockImplementation(async () => undefined);
  boardApiMocks.updateBoardCard.mockImplementation(async () => cloneCard(mockState.cards[0] ?? createCardFixture()));
  boardApiMocks.deleteBoardCard.mockResolvedValue(undefined);
  boardApiMocks.archiveBoardCard.mockImplementation(async () => cloneCard(mockState.cards[0] ?? createCardFixture()));
  boardApiMocks.moveBoardCard.mockImplementation(async (boardId: string, cardId: string, request: MoveBoardCardRequest) => {
    const card = mockState.cards.find((candidate) => candidate.id === cardId && candidate.boardId === boardId);
    if (!card) {
      throw new Error("Card not found.");
    }

    card.laneId = request.laneId;
    card.position = request.position;
    card.updatedAt = createTimestamp(17);

    return cloneCard(card);
  });
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

async function dragCardToLane(wrapper: VueWrapper, sourceColumnIndex: number, targetColumnIndex: number): Promise<void> {
  const sourceColumn = wrapper.findAll(".kanban-col")[sourceColumnIndex];
  const targetColumn = wrapper.findAll(".kanban-col")[targetColumnIndex];
  const dataTransfer = {
    dropEffect: "none",
    effectAllowed: "all",
    setData: vi.fn(),
  };

  await sourceColumn.get(".k-card").trigger("dragstart", { dataTransfer });
  await targetColumn.get(".kanban-col__cards").trigger("dragenter", { dataTransfer });
  await targetColumn.get(".kanban-col__cards").trigger("dragover", {
    dataTransfer,
    preventDefault: vi.fn(),
  });
  await targetColumn.get(".kanban-col__cards").trigger("drop", {
    dataTransfer,
    preventDefault: vi.fn(),
  });
  await flushPromises();
}

describe("KanbanBoard", () => {
  beforeEach(() => {
    mockState = {
      boards: [createBoardFixture()],
      lanes: [createLaneFixture({ id: "lane-backlog", name: "Backlog", isInbox: true })],
      cards: [],
    };
    configureBoardApiMocks();
    bookmarkState.refresh.mockClear();
    bookmarkState.error.value = null;
    bookmarkState.isLoading.value = false;
    bookmarkState.bookmarks.value = [
      {
        fullName: "acme/rocket",
        owner: "acme",
        name: "rocket",
      },
    ];
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

    await getButtonByText(wrapper, "Edit Board").trigger("click");
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

  it("renders work mode by default and exposes manage controls only in manage mode", async () => {
    mockState = {
      boards: [createBoardFixture()],
      lanes: [createLaneFixture({ id: "lane-backlog", name: "Backlog", isInbox: true })],
      cards: [createCardFixture({ laneId: "lane-backlog" })],
    };
    configureBoardApiMocks();

    const wrapper = mountBoard();
    const boardStore = useBoardStore();
    await flushPromises();

    expect(boardStore.boardMode).toBe("work");
    expect(getButtonByText(wrapper, "Edit Board").attributes("aria-pressed")).toBe("false");
    expect(wrapper.text()).not.toContain("Add Lane");
    expect(wrapper.text()).not.toContain("Rename board");
    expect(wrapper.find("[data-testid='board-source-form']").exists()).toBe(false);
    expect(wrapper.text()).toContain("+ Add a card");

    boardStore.setBoardMode("manage");
    await flushPromises();

    expect(getButtonByText(wrapper, "Save").attributes("aria-pressed")).toBe("true");
    expect(wrapper.text()).toContain("Add Lane");
    expect(wrapper.text()).toContain("Rename board");
    expect(wrapper.find("[data-testid='board-source-form']").exists()).toBe(true);
    expect(wrapper.text()).toContain("+ Add another lane");
  });

  it("cancels manage-only forms when switching back to work mode", async () => {
    const wrapper = mountBoard();
    const boardStore = useBoardStore();
    await flushPromises();

    boardStore.setBoardMode("manage");
    await flushPromises();

    await getButtonByText(wrapper, "Add Lane").trigger("click");
    await wrapper.get(".kanban-lane-creator__input").setValue("Ready");
    await getButtonByText(wrapper, "Rename board").trigger("click");
    await wrapper.get(".kanban-header__rename-input").setValue("Renamed board draft");

    boardStore.setBoardMode("work");
    await flushPromises();

    expect(wrapper.find(".kanban-lane-creator__form").exists()).toBe(false);
    expect(wrapper.find(".kanban-header__rename-form").exists()).toBe(false);
    expect(wrapper.text()).toContain("Delivery Board");

    boardStore.setBoardMode("manage");
    await flushPromises();

    await getButtonByText(wrapper, "Add Lane").trigger("click");
    expect((wrapper.get(".kanban-lane-creator__input").element as HTMLInputElement).value).toBe("");

    await getButtonByText(wrapper, "Rename board").trigger("click");
    expect((wrapper.get(".kanban-header__rename-input").element as HTMLInputElement).value).toBe("Delivery Board");
  });

  it("keeps card drag-and-drop working in work mode", async () => {
    mockState = {
      boards: [createBoardFixture()],
      lanes: [
        createLaneFixture({ id: "lane-backlog", name: "Backlog", isInbox: true, position: POSITION_GAP }),
        createLaneFixture({ id: "lane-ready", name: "Ready", position: POSITION_GAP * 2 }),
      ],
      cards: [createCardFixture({ id: "card-1", laneId: "lane-backlog", title: "Move me" })],
    };
    configureBoardApiMocks();

    const wrapper = mountBoard();
    const boardStore = useBoardStore();
    await flushPromises();

    expect(boardStore.boardMode).toBe("work");
    expect(wrapper.text()).not.toContain("Rename board");

    await dragCardToLane(wrapper, 0, 1);

    expect(boardApiMocks.moveBoardCard).toHaveBeenCalledWith("board-1", "card-1", {
      laneId: "lane-ready",
      position: 0,
    });
    expect(wrapper.findAll(".kanban-col")[1]!.text()).toContain("Move me");
  });

  it("shows structural controls and keeps card drag-and-drop working in manage mode", async () => {
    mockState = {
      boards: [createBoardFixture()],
      lanes: [
        createLaneFixture({ id: "lane-backlog", name: "Backlog", isInbox: true, position: POSITION_GAP }),
        createLaneFixture({ id: "lane-ready", name: "Ready", position: POSITION_GAP * 2 }),
      ],
      cards: [createCardFixture({ id: "card-1", laneId: "lane-backlog", title: "Move me" })],
    };
    configureBoardApiMocks();

    const wrapper = mountBoard();
    const boardStore = useBoardStore();
    await flushPromises();

    boardStore.setBoardMode("manage");
    await flushPromises();

    expect(wrapper.text()).toContain("Rename board");
    expect(wrapper.text()).toContain("Add Lane");
    expect(wrapper.text()).toContain("+ Add another lane");
    expect(wrapper.findAll(".kanban-col__lane-actions")).toHaveLength(2);

    await dragCardToLane(wrapper, 0, 1);

    expect(boardApiMocks.moveBoardCard).toHaveBeenCalledWith("board-1", "card-1", {
      laneId: "lane-ready",
      position: 0,
    });
    expect(wrapper.findAll(".kanban-col")[1]!.text()).toContain("Move me");
  });

  it("keeps the mode toggle available during mutations", async () => {
    const wrapper = mountBoard();
    const boardStore = useBoardStore();
    const sidebarStore = useSidebarStore();
    await flushPromises();

    sidebarStore.setActiveRail("board");
    boardStore.pendingMutations = 1;
    await nextTick();

    const toggleButton = getButtonByText(wrapper, "Edit Board");
    expect(toggleButton.attributes("disabled")).toBeUndefined();

    await toggleButton.trigger("click");

    expect(boardStore.boardMode).toBe("manage");
    expect(getButtonByText(wrapper, "Save").attributes("aria-pressed")).toBe("true");
  });

  it("disables the mode toggle until the board finishes loading", async () => {
    boardApiMocks.listBoards.mockImplementation(() => new Promise<Board[]>(() => undefined));

    const wrapper = mountBoard();
    const boardStore = useBoardStore();
    await nextTick();

    const toggleButton = getButtonByText(wrapper, "Edit Board");
    expect(boardStore.isLoaded).toBe(false);
    expect(toggleButton.attributes("disabled")).toBeDefined();
    expect(wrapper.text()).toContain("Loading board…");
  });
});
