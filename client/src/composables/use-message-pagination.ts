import { readonly, shallowRef, type ShallowRef } from "vue"
import type { AccumulatedMessage } from "@/lib/client-types"
import { api } from "@/api/client"
import { convertFleetMessageToAccumulated, type FleetMessage } from "@/lib/pagination-utils"
import type { PaginationSnapshot } from "@/lib/session-cache"

export type { PaginationSnapshot } from "@/lib/session-cache"

export interface PaginationState {
  hasMore: Readonly<ShallowRef<boolean>>
  isLoadingOlder: Readonly<ShallowRef<boolean>>
  oldestMessageId: Readonly<ShallowRef<string | null>>
  totalCount: Readonly<ShallowRef<number | null>>
  loadError: Readonly<ShallowRef<string | null>>
}

export interface UseMessagePaginationReturn extends PaginationState {
  loadInitialMessages: (
    sessionId: string,
    instanceId: string,
    signal?: AbortSignal,
  ) => Promise<AccumulatedMessage[]>
  loadOlderMessages: (
    sessionId: string,
    instanceId: string,
  ) => Promise<AccumulatedMessage[]>
  resetPagination: () => void
  snapshotPagination: () => PaginationSnapshot
  hydratePagination: (snapshot: PaginationSnapshot) => void
}

const DEFAULT_PAGE_SIZE = 10
const MIN_FETCH_INTERVAL_MS = 500

export function useMessagePagination(): UseMessagePaginationReturn {
  const hasMore = shallowRef(false)
  const isLoadingOlder = shallowRef(false)
  const oldestMessageId = shallowRef<string | null>(null)
  const totalCount = shallowRef<number | null>(null)
  const loadError = shallowRef<string | null>(null)

  let lastFetchTime = 0

  async function loadInitialMessages(
    sessionId: string,
    instanceId: string,
    signal?: AbortSignal,
  ): Promise<AccumulatedMessage[]> {
    try {
      const { data, error, response } = await api.GET("/api/sessions/{id}/messages", {
        params: {
          path: { id: sessionId },
          query: {
            instanceId,
            limit: DEFAULT_PAGE_SIZE,
          },
        },
        signal,
      });

      if (error || !response.ok) {
        loadError.value = "Failed to load initial messages";
        return [];
      }

      // Response body is not typed in schema, use data from openapi-fetch
      const responseData = data as unknown as {
        messages: FleetMessage[]
        pagination: {
          hasMore: boolean
          oldestMessageId: string | null
          totalCount: number
        }
      }

      hasMore.value = responseData.pagination?.hasMore ?? false
      oldestMessageId.value = responseData.pagination?.oldestMessageId ?? null
      totalCount.value = responseData.pagination?.totalCount ?? responseData.messages?.length ?? null
      loadError.value = null

      // Preserve OpenCode response order (oldest-first)
      return (responseData.messages ?? []).map(convertFleetMessageToAccumulated)
    } catch (error) {
      if (error instanceof DOMException && error.name === "AbortError") {
        return []
      }

      loadError.value = "Failed to load initial messages"
      return []
    }
  }

  async function loadOlderMessages(sessionId: string, instanceId: string): Promise<AccumulatedMessage[]> {
    if (!hasMore.value || isLoadingOlder.value) {
      return []
    }

    const now = Date.now()
    if (now - lastFetchTime < MIN_FETCH_INTERVAL_MS) {
      return []
    }

    isLoadingOlder.value = true
    lastFetchTime = now

    try {
      const { data, error, response } = await api.GET("/api/sessions/{id}/messages", {
        params: {
          path: { id: sessionId },
          query: {
            instanceId,
            limit: DEFAULT_PAGE_SIZE,
            ...(oldestMessageId.value ? { before: oldestMessageId.value } : {}),
          },
        },
      });

      if (error || !response.ok) {
        loadError.value = "Failed to load older messages";
        return [];
      }

      // Response body is not typed in schema, use data from openapi-fetch
      const responseData = data as unknown as {
        messages: FleetMessage[]
        pagination: {
          hasMore: boolean
          oldestMessageId: string | null
          totalCount: number
        }
      }

      hasMore.value = responseData.pagination?.hasMore ?? false
      oldestMessageId.value = responseData.pagination?.oldestMessageId ?? null
      totalCount.value = responseData.pagination?.totalCount ?? responseData.messages?.length ?? null
      loadError.value = null

      // Preserve OpenCode response order (oldest-first)
      return (responseData.messages ?? []).map(convertFleetMessageToAccumulated)
    } catch {
      loadError.value = "Failed to load older messages"
      return []
    } finally {
      isLoadingOlder.value = false
    }
  }

  function resetPagination(): void {
    hasMore.value = false
    isLoadingOlder.value = false
    oldestMessageId.value = null
    totalCount.value = null
    loadError.value = null
    lastFetchTime = 0
  }

  function snapshotPagination(): PaginationSnapshot {
    return {
      hasMore: hasMore.value,
      oldestMessageId: oldestMessageId.value,
      totalCount: totalCount.value,
    }
  }

  function hydratePagination(snapshot: PaginationSnapshot): void {
    hasMore.value = snapshot.hasMore
    oldestMessageId.value = snapshot.oldestMessageId
    totalCount.value = snapshot.totalCount
    loadError.value = null
  }

  return {
    hasMore: readonly(hasMore),
    isLoadingOlder: readonly(isLoadingOlder),
    oldestMessageId: readonly(oldestMessageId),
    totalCount: readonly(totalCount),
    loadError: readonly(loadError),
    loadInitialMessages,
    loadOlderMessages,
    resetPagination,
    snapshotPagination,
    hydratePagination,
  }
}
