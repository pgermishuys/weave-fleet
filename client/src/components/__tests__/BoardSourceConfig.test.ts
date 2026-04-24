import { flushPromises, mount } from "@vue/test-utils";
import { beforeEach, describe, expect, it, vi } from "vitest";
import type { BoardSource, CreateBoardSourceRequest } from "@/lib/board-api";

const boardApiMocks = vi.hoisted(() => ({
  createBoardSource: vi.fn(),
  deleteBoardSource: vi.fn(),
  listBoardSources: vi.fn(),
}));

const bookmarkState = vi.hoisted(() => ({
  bookmarks: null as unknown as { value: Array<{ fullName: string; owner: string; name: string }> },
  error: null as unknown as { value: string | null },
  isLoading: null as unknown as { value: boolean },
  refresh: vi.fn(async () => undefined),
}));

vi.mock("@/lib/board-api", () => ({
  createBoardSource: boardApiMocks.createBoardSource,
  deleteBoardSource: boardApiMocks.deleteBoardSource,
  listBoardSources: boardApiMocks.listBoardSources,
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

import BoardSourceConfig from "@/components/board/BoardSourceConfig.vue";

let mockSources: BoardSource[];

function createTimestamp(minute: number): string {
  return new Date(Date.UTC(2026, 3, 24, 14, minute, 0)).toISOString();
}

function createSourceFixture(overrides: Partial<BoardSource> = {}): BoardSource {
  return {
    id: "source-1",
    boardId: "board-1",
    providerType: "github",
    config: JSON.stringify({ repository: "acme/rocket", labels: "bug, priority:high" }),
    lastSyncAt: null,
    createdAt: createTimestamp(0),
    updatedAt: createTimestamp(0),
    ...overrides,
  };
}

describe("BoardSourceConfig", () => {
  beforeEach(() => {
    mockSources = [];

    boardApiMocks.listBoardSources.mockReset();
    boardApiMocks.createBoardSource.mockReset();
    boardApiMocks.deleteBoardSource.mockReset();
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

    boardApiMocks.listBoardSources.mockImplementation(async () => [...mockSources]);
    boardApiMocks.createBoardSource.mockImplementation(async (boardId: string, request: CreateBoardSourceRequest) => {
      const createdSource = createSourceFixture({
        id: `source-${mockSources.length + 1}`,
        boardId,
        config: request.config,
        createdAt: createTimestamp(1),
        updatedAt: createTimestamp(1),
      });

      mockSources.push(createdSource);
      return createdSource;
    });
    boardApiMocks.deleteBoardSource.mockImplementation(async (_boardId: string, sourceId: string) => {
      mockSources = mockSources.filter((source) => source.id !== sourceId);
    });
  });

  it("adds a repo source, lists it, and removes it", async () => {
    const wrapper = mount(BoardSourceConfig, {
      props: {
        boardId: "board-1",
      },
    });

    await flushPromises();

    await wrapper.get('[data-testid="board-source-repo-select"]').setValue("acme/rocket");
    await wrapper.get('[data-testid="board-source-label-filter"]').setValue("bug, priority:high");
    await wrapper.get('[data-testid="board-source-form"]').trigger("submit");
    await flushPromises();

    expect(boardApiMocks.createBoardSource).toHaveBeenCalledWith("board-1", {
      providerType: "github",
      config: JSON.stringify({ repository: "acme/rocket", labels: "bug, priority:high" }),
    });

    const sourceItems = wrapper.findAll('[data-testid="board-source-item"]');
    expect(sourceItems).toHaveLength(1);
    expect(wrapper.text()).toContain("acme/rocket");
    expect(wrapper.text()).toContain("Labels: bug, priority:high");
    expect(wrapper.text()).toContain("Any assignee");
    expect(wrapper.text()).toContain("Last sync: Not synced yet");

    await wrapper.get('[data-testid="board-source-remove"]').trigger("click");
    await flushPromises();

    expect(boardApiMocks.deleteBoardSource).toHaveBeenCalledWith("board-1", "source-1");
    expect(wrapper.findAll('[data-testid="board-source-item"]')).toHaveLength(0);
    expect(wrapper.get('[data-testid="board-source-empty"]').text()).toContain("No configured sources yet.");
  });

  it("adds an assigned-to-me source with assignee in config json", async () => {
    const wrapper = mount(BoardSourceConfig, {
      props: {
        boardId: "board-1",
      },
    });

    await flushPromises();

    await wrapper.get('[data-testid="board-source-repo-select"]').setValue("acme/rocket");
    await wrapper.get('[data-testid="board-source-assigned-to-me"]').setValue(true);
    await wrapper.get('[data-testid="board-source-form"]').trigger("submit");
    await flushPromises();

    expect(boardApiMocks.createBoardSource).toHaveBeenCalledWith("board-1", {
      providerType: "github",
      config: JSON.stringify({ repository: "acme/rocket", assignee: "@me" }),
    });

    expect(wrapper.text()).toContain("Assignee: @me");
  });
});
