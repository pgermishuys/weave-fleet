import { beforeEach, describe, expect, it, vi } from "vitest";
import type {
  BoardSource,
  BoardSyncResult,
  CreateBoardSourceRequest,
  UpdateBoardSourceRequest,
} from "@/lib/board-api";
import {
  createBoardSource,
  deleteBoardSource,
  listBoardSources,
  syncBoard,
  updateBoardSource,
} from "@/lib/board-api";

const { apiFetchMock } = vi.hoisted(() => ({
  apiFetchMock: vi.fn(),
}));

vi.mock("@/lib/api-client", () => ({
  apiFetch: apiFetchMock,
}));

function createJsonResponse<T>(body: T, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "Content-Type": "application/json" },
  });
}

function createBoardSourceFixture(overrides: Partial<BoardSource> = {}): BoardSource {
  return {
    id: "source-1",
    boardId: "board-1",
    providerType: "github",
    config: '{"repository":"acme/rocket"}',
    lastSyncAt: null,
    createdAt: "2026-01-01T00:00:00Z",
    updatedAt: "2026-01-01T00:00:00Z",
    ...overrides,
  };
}

function createBoardSyncResultFixture(overrides: Partial<BoardSyncResult> = {}): BoardSyncResult {
  return {
    sourcesProcessed: 1,
    issuesFetched: 2,
    cardsCreated: 3,
    cardsUpdated: 4,
    cardsMarkedStale: 5,
    syncedAt: "2026-01-02T00:00:00Z",
    ...overrides,
  };
}

describe("board-api source endpoints", () => {
  beforeEach(() => {
    apiFetchMock.mockReset();
  });

  it("lists board sources with typed results", async () => {
    const sources: BoardSource[] = [createBoardSourceFixture()];
    apiFetchMock.mockResolvedValue(createJsonResponse(sources));

    const result = await listBoardSources("board-1");

    expect(apiFetchMock).toHaveBeenCalledWith("/api/boards/board-1/sources");
    expect(result).toEqual(sources);
  });

  it("creates a board source with the expected request body", async () => {
    const request: CreateBoardSourceRequest = {
      providerType: "github",
      config: '{"repository":"acme/rocket"}',
    };
    const createdSource: BoardSource = createBoardSourceFixture();
    apiFetchMock.mockResolvedValue(createJsonResponse(createdSource, 201));

    const result = await createBoardSource("board-1", request);

    expect(apiFetchMock).toHaveBeenCalledWith("/api/boards/board-1/sources", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(request),
    });
    expect(result).toEqual(createdSource);
  });

  it("updates a board source with the expected request body", async () => {
    const request: UpdateBoardSourceRequest = {
      config: '{"repository":"acme/rocket","labels":["bug"]}',
    };
    const updatedSource: BoardSource = createBoardSourceFixture({
      config: request.config ?? '{"repository":"acme/rocket"}',
    });
    apiFetchMock.mockResolvedValue(createJsonResponse(updatedSource));

    const result = await updateBoardSource("board-1", "source-1", request);

    expect(apiFetchMock).toHaveBeenCalledWith("/api/boards/board-1/sources/source-1", {
      method: "PATCH",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(request),
    });
    expect(result).toEqual(updatedSource);
  });

  it("deletes a board source", async () => {
    apiFetchMock.mockResolvedValue(new Response(null, { status: 204 }));

    await deleteBoardSource("board-1", "source-1");

    expect(apiFetchMock).toHaveBeenCalledWith("/api/boards/board-1/sources/source-1", {
      method: "DELETE",
    });
  });

  it("syncs a board and returns typed sync counts", async () => {
    const syncResult: BoardSyncResult = createBoardSyncResultFixture();
    apiFetchMock.mockResolvedValue(createJsonResponse(syncResult));

    const result = await syncBoard("board-1");

    expect(apiFetchMock).toHaveBeenCalledWith("/api/boards/board-1/sync", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({}),
    });
    expect(result).toEqual(syncResult);
  });

  it("surfaces API errors for source operations", async () => {
    apiFetchMock.mockResolvedValue(createJsonResponse({ error: "Source not found." }, 404));

    await expect(deleteBoardSource("board-1", "missing-source")).rejects.toThrow("Source not found.");
  });
});
