import { apiFetch } from "@/lib/api-client";

export interface Board {
  id: string;
  name: string;
  createdAt: string;
  updatedAt: string;
}

export interface BoardLane {
  id: string;
  boardId: string;
  name: string;
  position: number;
  isInbox: boolean;
  createdAt: string;
  updatedAt: string;
}

export interface BoardCard {
  id: string;
  boardId: string;
  laneId: string;
  title: string;
  sourceType: string | null;
  sourceKey: string | null;
  metadata: string | null;
  position: number;
  archivedAt: string | null;
  createdAt: string;
  updatedAt: string;
}

export interface CreateBoardRequest {
  name: string;
}

export interface UpdateBoardRequest {
  name: string;
}

export interface CreateBoardLaneRequest {
  name: string;
  position?: number;
}

export interface UpdateBoardLaneRequest {
  name?: string;
  position?: number;
  isInbox?: boolean;
}

export interface ReorderBoardLanesRequest {
  laneIds: readonly string[];
}

export interface CreateBoardCardRequest {
  laneId: string;
  title: string;
  position?: number;
}

export interface UpdateBoardCardRequest {
  title?: string;
  laneId?: string;
  position?: number;
}

export interface MoveBoardCardRequest {
  laneId: string;
  position: number;
}

interface ApiErrorBody {
  error?: string;
  detail?: string;
  title?: string;
}

function buildBoardPath(): string {
  return "/api/boards";
}

function buildBoardByIdPath(boardId: string): string {
  return `${buildBoardPath()}/${encodeURIComponent(boardId)}`;
}

function buildBoardLanesPath(boardId: string): string {
  return `${buildBoardByIdPath(boardId)}/lanes`;
}

function buildBoardLaneByIdPath(boardId: string, laneId: string): string {
  return `${buildBoardLanesPath(boardId)}/${encodeURIComponent(laneId)}`;
}

function buildBoardCardsPath(boardId: string): string {
  return `${buildBoardByIdPath(boardId)}/cards`;
}

function buildBoardCardByIdPath(boardId: string, cardId: string): string {
  return `${buildBoardCardsPath(boardId)}/${encodeURIComponent(cardId)}`;
}

async function readErrorMessage(response: Response, fallbackMessage: string): Promise<string> {
  const bodyText = await response.text().catch(() => "");
  if (!bodyText) {
    return fallbackMessage;
  }

  try {
    const body = JSON.parse(bodyText) as ApiErrorBody;

    if (typeof body.error === "string" && body.error.trim().length > 0) {
      return body.error;
    }

    if (typeof body.detail === "string" && body.detail.trim().length > 0) {
      return body.detail;
    }

    if (typeof body.title === "string" && body.title.trim().length > 0) {
      return body.title;
    }
  } catch {
    if (bodyText.trim().length > 0) {
      return bodyText.trim();
    }
  }

  return fallbackMessage;
}

async function getJson<TResponse>(path: string, fallbackMessage: string): Promise<TResponse> {
  const response = await apiFetch(path);
  if (!response.ok) {
    throw new Error(await readErrorMessage(response, fallbackMessage));
  }

  return (await response.json()) as TResponse;
}

async function sendJson<TRequest, TResponse>(
  path: string,
  method: "POST" | "PATCH",
  body: TRequest,
  fallbackMessage: string,
): Promise<TResponse> {
  const response = await apiFetch(path, {
    method,
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(body),
  });

  if (!response.ok) {
    throw new Error(await readErrorMessage(response, fallbackMessage));
  }

  return (await response.json()) as TResponse;
}

async function sendNoContent(path: string, method: "DELETE" | "PATCH", fallbackMessage: string): Promise<void> {
  const response = await apiFetch(path, { method });
  if (!response.ok) {
    throw new Error(await readErrorMessage(response, fallbackMessage));
  }
}

export async function listBoards(): Promise<Board[]> {
  return getJson<Board[]>(buildBoardPath(), "Failed to load boards.");
}

export async function createBoard(request: CreateBoardRequest): Promise<Board> {
  return sendJson<CreateBoardRequest, Board>(buildBoardPath(), "POST", request, "Failed to create board.");
}

export async function updateBoard(boardId: string, request: UpdateBoardRequest): Promise<Board> {
  return sendJson<UpdateBoardRequest, Board>(buildBoardByIdPath(boardId), "PATCH", request, "Failed to update board.");
}

export async function deleteBoard(boardId: string): Promise<void> {
  return sendNoContent(buildBoardByIdPath(boardId), "DELETE", "Failed to delete board.");
}

export async function listBoardLanes(boardId: string): Promise<BoardLane[]> {
  return getJson<BoardLane[]>(buildBoardLanesPath(boardId), "Failed to load board lanes.");
}

export async function createBoardLane(boardId: string, request: CreateBoardLaneRequest): Promise<BoardLane> {
  return sendJson<CreateBoardLaneRequest, BoardLane>(
    buildBoardLanesPath(boardId),
    "POST",
    request,
    "Failed to create board lane.",
  );
}

export async function updateBoardLane(boardId: string, laneId: string, request: UpdateBoardLaneRequest): Promise<BoardLane> {
  return sendJson<UpdateBoardLaneRequest, BoardLane>(
    buildBoardLaneByIdPath(boardId, laneId),
    "PATCH",
    request,
    "Failed to update board lane.",
  );
}

export async function deleteBoardLane(boardId: string, laneId: string): Promise<void> {
  return sendNoContent(buildBoardLaneByIdPath(boardId, laneId), "DELETE", "Failed to delete board lane.");
}

export async function reorderBoardLanes(boardId: string, request: ReorderBoardLanesRequest): Promise<void> {
  const response = await apiFetch(`${buildBoardLanesPath(boardId)}/reorder`, {
    method: "PATCH",
    headers: { "Content-Type": "application/json" },
    body: JSON.stringify(request),
  });

  if (!response.ok) {
    throw new Error(await readErrorMessage(response, "Failed to reorder board lanes."));
  }
}

export async function listBoardCards(boardId: string): Promise<BoardCard[]> {
  return getJson<BoardCard[]>(buildBoardCardsPath(boardId), "Failed to load board cards.");
}

export async function createBoardCard(boardId: string, request: CreateBoardCardRequest): Promise<BoardCard> {
  return sendJson<CreateBoardCardRequest, BoardCard>(
    buildBoardCardsPath(boardId),
    "POST",
    request,
    "Failed to create board card.",
  );
}

export async function updateBoardCard(boardId: string, cardId: string, request: UpdateBoardCardRequest): Promise<BoardCard> {
  return sendJson<UpdateBoardCardRequest, BoardCard>(
    buildBoardCardByIdPath(boardId, cardId),
    "PATCH",
    request,
    "Failed to update board card.",
  );
}

export async function deleteBoardCard(boardId: string, cardId: string): Promise<void> {
  return sendNoContent(buildBoardCardByIdPath(boardId, cardId), "DELETE", "Failed to delete board card.");
}

export async function archiveBoardCard(boardId: string, cardId: string): Promise<BoardCard> {
  return sendJson<Record<string, never>, BoardCard>(
    `${buildBoardCardByIdPath(boardId, cardId)}/archive`,
    "POST",
    {},
    "Failed to archive board card.",
  );
}

export async function moveBoardCard(boardId: string, cardId: string, request: MoveBoardCardRequest): Promise<BoardCard> {
  return sendJson<MoveBoardCardRequest, BoardCard>(
    `${buildBoardCardByIdPath(boardId, cardId)}/move`,
    "POST",
    request,
    "Failed to move board card.",
  );
}
