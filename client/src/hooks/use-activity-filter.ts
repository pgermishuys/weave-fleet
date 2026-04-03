"use client";

import { useState, useMemo, useCallback } from "react";
import type { AccumulatedMessage, AccumulatedPart } from "@/lib/api-types";

export type MessageTypeFilter = "user" | "assistant" | "tool";

export interface UseActivityFilterReturn {
  searchQuery: string;
  setSearchQuery: (q: string) => void;
  messageTypeFilter: Set<MessageTypeFilter>;
  toggleMessageType: (type: MessageTypeFilter) => void;
  agentFilter: string | null;
  setAgentFilter: (agent: string | null) => void;
  filteredMessages: AccumulatedMessage[];
  matchingPartIds: Set<string>;
  isFiltering: boolean;
  clearFilters: () => void;
  isOpen: boolean;
  setIsOpen: (open: boolean) => void;
}

const DEFAULT_TYPE_FILTER: Set<MessageTypeFilter> = new Set(["user", "assistant", "tool"]);

/**
 * Determines whether an assistant message passes the type filter.
 *
 * - "assistant" in set → messages that have at least one text part
 * - "tool"      in set → messages that have at least one tool part
 * - An assistant message with both text and tool parts shows if either is active.
 *
 * @internal exported for unit testing
 */
export function assistantPassesTypeFilter(
  message: AccumulatedMessage,
  filter: Set<MessageTypeFilter>
): boolean {
  const hasText = message.parts.some((p) => p.type === "text");
  const hasTool = message.parts.some((p) => p.type === "tool");
  if (hasText && filter.has("assistant")) return true;
  if (hasTool && filter.has("tool")) return true;
  // If the message has neither, still show it when either assistant or tool is active
  if (!hasText && !hasTool) {
    return filter.has("assistant") || filter.has("tool");
  }
  return false;
}

/**
 * Returns the text content of a part that should be searched.
 *
 * @internal exported for unit testing
 */
export function getPartSearchableText(part: AccumulatedPart): string {
  if (part.type === "text") {
    return part.text;
  }
  if (part.type === "file") {
    return part.filename ?? "";
  }
  // Tool part: match tool name and output
  // eslint-disable-next-line @typescript-eslint/no-explicit-any
  const state = part.state as any;
  const output = typeof state?.output === "string" ? state.output : "";
  return `${part.tool} ${output}`;
}

/**
 * Checks if a part's content matches the query (case-insensitive).
 *
 * @internal exported for unit testing
 */
export function partMatchesQuery(part: AccumulatedPart, lowerQuery: string): boolean {
  return getPartSearchableText(part).toLowerCase().includes(lowerQuery);
}

export function useActivityFilter(messages: AccumulatedMessage[]): UseActivityFilterReturn {
  const [searchQuery, setSearchQuery] = useState("");
  const [messageTypeFilter, setMessageTypeFilter] =
    useState<Set<MessageTypeFilter>>(DEFAULT_TYPE_FILTER);
  const [agentFilter, setAgentFilter] = useState<string | null>(null);
  const [isOpen, setIsOpen] = useState(false);

  const toggleMessageType = useCallback((type: MessageTypeFilter) => {
    setMessageTypeFilter((prev) => {
      if (prev.has(type)) {
        // Don't remove the last active type
        if (prev.size <= 1) return prev;
        const next = new Set(prev);
        next.delete(type);
        return next;
      }
      const next = new Set(prev);
      next.add(type);
      return next;
    });
  }, []);

  const clearFilters = useCallback(() => {
    setSearchQuery("");
    setMessageTypeFilter(DEFAULT_TYPE_FILTER);
    setAgentFilter(null);
  }, []);

  const isFiltering = useMemo(() => {
    return (
      searchQuery.trim() !== "" ||
      agentFilter !== null ||
      messageTypeFilter.size !== DEFAULT_TYPE_FILTER.size ||
      !([...DEFAULT_TYPE_FILTER] as MessageTypeFilter[]).every((t) => messageTypeFilter.has(t))
    );
  }, [searchQuery, agentFilter, messageTypeFilter]);

  // Build the set of matching part IDs for the current search query
  const matchingPartIds = useMemo<Set<string>>(() => {
    const lowerQuery = searchQuery.trim().toLowerCase();
    if (!lowerQuery) return new Set();
    const ids = new Set<string>();
    for (const message of messages) {
      for (const part of message.parts) {
        if (partMatchesQuery(part, lowerQuery)) {
          ids.add(part.partId);
        }
      }
    }
    return ids;
  }, [messages, searchQuery]);

  const filteredMessages = useMemo<AccumulatedMessage[]>(() => {
    const lowerQuery = searchQuery.trim().toLowerCase();

    return messages.filter((message) => {
      // ── Role / type filter ──────────────────────────────────────────────────
      if (message.role === "user") {
        if (!messageTypeFilter.has("user")) return false;
      } else {
        if (!assistantPassesTypeFilter(message, messageTypeFilter)) return false;
      }

      // ── Agent filter ────────────────────────────────────────────────────────
      if (agentFilter !== null && message.agent !== agentFilter) return false;

      // ── Search query ────────────────────────────────────────────────────────
      if (lowerQuery) {
        const anyPartMatches = message.parts.some((part) =>
          partMatchesQuery(part, lowerQuery)
        );
        if (!anyPartMatches) return false;
      }

      return true;
    });
  }, [messages, searchQuery, messageTypeFilter, agentFilter]);

  return {
    searchQuery,
    setSearchQuery,
    messageTypeFilter,
    toggleMessageType,
    agentFilter,
    setAgentFilter,
    filteredMessages,
    matchingPartIds,
    isFiltering,
    clearFilters,
    isOpen,
    setIsOpen,
  };
}
