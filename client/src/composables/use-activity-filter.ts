import { computed, readonly, shallowRef, type ComputedRef, type ShallowRef } from "vue"
import type { AccumulatedMessage, AccumulatedPart } from "@/lib/api-types"

export type MessageTypeFilter = "user" | "assistant" | "tool"

export interface UseActivityFilterReturn {
  searchQuery: Readonly<ShallowRef<string>>
  setSearchQuery: (query: string) => void
  messageTypeFilter: Readonly<ShallowRef<ReadonlySet<MessageTypeFilter>>>
  toggleMessageType: (type: MessageTypeFilter) => void
  agentFilter: Readonly<ShallowRef<string | null>>
  setAgentFilter: (agent: string | null) => void
  filteredMessages: ComputedRef<AccumulatedMessage[]>
  matchingPartIds: ComputedRef<Set<string>>
  isFiltering: ComputedRef<boolean>
  clearFilters: () => void
  isOpen: Readonly<ShallowRef<boolean>>
  setIsOpen: (open: boolean) => void
}

const DEFAULT_TYPE_FILTER = new Set<MessageTypeFilter>(["user", "assistant", "tool"])

export function assistantPassesTypeFilter(
  message: AccumulatedMessage,
  filter: Set<MessageTypeFilter>,
): boolean {
  const hasText = message.parts.some((part) => part.type === "text")
  const hasTool = message.parts.some((part) => part.type === "tool")

  if (hasText && filter.has("assistant")) {
    return true
  }

  if (hasTool && filter.has("tool")) {
    return true
  }

  if (!hasText && !hasTool) {
    return filter.has("assistant") || filter.has("tool")
  }

  return false
}

export function getPartSearchableText(part: AccumulatedPart): string {
  if (part.type === "text") {
    return part.text
  }

  if (part.type === "reasoning") {
    return ""
  }

  if (part.type === "file") {
    return part.filename ?? ""
  }

  const state = part.state as { output?: unknown } | undefined
  const output = typeof state?.output === "string" ? state.output : ""
  return `${part.tool} ${output}`
}

export function partMatchesQuery(part: AccumulatedPart, lowerQuery: string): boolean {
  return getPartSearchableText(part).toLowerCase().includes(lowerQuery)
}

export function useActivityFilter(messages: AccumulatedMessage[]): UseActivityFilterReturn {
  const searchQuery = shallowRef("")
  const messageTypeFilter = shallowRef(new Set<MessageTypeFilter>(DEFAULT_TYPE_FILTER))
  const agentFilter = shallowRef<string | null>(null)
  const isOpen = shallowRef(false)

  function setSearchQuery(query: string): void {
    searchQuery.value = query
  }

  function toggleMessageType(type: MessageTypeFilter): void {
    const next = new Set(messageTypeFilter.value)

    if (next.has(type)) {
      if (next.size <= 1) {
        return
      }

      next.delete(type)
    } else {
      next.add(type)
    }

    messageTypeFilter.value = next
  }

  function setAgentFilter(agent: string | null): void {
    agentFilter.value = agent
  }

  function setIsOpen(open: boolean): void {
    isOpen.value = open
  }

  function clearFilters(): void {
    searchQuery.value = ""
    messageTypeFilter.value = new Set<MessageTypeFilter>(DEFAULT_TYPE_FILTER)
    agentFilter.value = null
  }

  const isFiltering = computed(() => {
    if (searchQuery.value.trim() !== "" || agentFilter.value !== null) {
      return true
    }

    if (messageTypeFilter.value.size !== DEFAULT_TYPE_FILTER.size) {
      return true
    }

    return [...DEFAULT_TYPE_FILTER].some((type) => !messageTypeFilter.value.has(type))
  })

  const matchingPartIds = computed(() => {
    const lowerQuery = searchQuery.value.trim().toLowerCase()
    if (!lowerQuery) {
      return new Set<string>()
    }

    const ids = new Set<string>()
    for (const message of messages) {
      for (const part of message.parts) {
        if (partMatchesQuery(part, lowerQuery)) {
          ids.add(part.partId)
        }
      }
    }

    return ids
  })

  const filteredMessages = computed(() => {
    const lowerQuery = searchQuery.value.trim().toLowerCase()

    return messages.filter((message) => {
      if (message.role === "user") {
        if (!messageTypeFilter.value.has("user")) {
          return false
        }
      } else if (!assistantPassesTypeFilter(message, messageTypeFilter.value)) {
        return false
      }

      if (agentFilter.value !== null && message.agent !== agentFilter.value) {
        return false
      }

      if (lowerQuery) {
        const anyPartMatches = message.parts.some((part) => partMatchesQuery(part, lowerQuery))
        if (!anyPartMatches) {
          return false
        }
      }

      return true
    })
  })

  return {
    searchQuery: readonly(searchQuery),
    setSearchQuery,
    messageTypeFilter: readonly(messageTypeFilter),
    toggleMessageType,
    agentFilter: readonly(agentFilter),
    setAgentFilter,
    filteredMessages,
    matchingPartIds,
    isFiltering,
    clearFilters,
    isOpen: readonly(isOpen),
    setIsOpen,
  }
}
