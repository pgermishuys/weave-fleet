"use client";

import { useEffect, useRef } from "react";
import { Search, User, Bot, Wrench, X, ChevronDown } from "lucide-react";
import { Input } from "@/components/ui/input";
import { Button } from "@/components/ui/button";
import {
  DropdownMenu,
  DropdownMenuTrigger,
  DropdownMenuContent,
  DropdownMenuItem,
} from "@/components/ui/dropdown-menu";
import type { AutocompleteAgent } from "@/lib/api-types";
import type { MessageTypeFilter } from "@/hooks/use-activity-filter";

interface ActivityStreamToolbarProps {
  searchQuery: string;
  setSearchQuery: (q: string) => void;
  messageTypeFilter: Set<MessageTypeFilter>;
  toggleMessageType: (type: MessageTypeFilter) => void;
  agentFilter: string | null;
  setAgentFilter: (agent: string | null) => void;
  isFiltering: boolean;
  clearFilters: () => void;
  filteredCount: number;
  totalCount: number;
  agents?: AutocompleteAgent[];
  onClose: () => void;
}

export function ActivityStreamToolbar({
  searchQuery,
  setSearchQuery,
  messageTypeFilter,
  toggleMessageType,
  agentFilter,
  setAgentFilter,
  isFiltering,
  clearFilters,
  filteredCount,
  totalCount,
  agents,
  onClose,
}: ActivityStreamToolbarProps) {
  const inputRef = useRef<HTMLInputElement>(null);

  // Auto-focus input when toolbar opens
  useEffect(() => {
    inputRef.current?.focus();
  }, []);

  return (
    <div className="flex items-center gap-2 px-4 py-1 border-b border-border/40 bg-background/80 backdrop-blur-sm">
      {/* Search icon + input */}
      <div className="relative flex items-center flex-1 min-w-0">
        <Search className="absolute left-2.5 h-3.5 w-3.5 text-muted-foreground pointer-events-none shrink-0" />
        <Input
          ref={inputRef}
          type="text"
          value={searchQuery}
          onChange={(e) => setSearchQuery(e.target.value)}
          placeholder="Search messages…"
          className="h-7 pl-8 pr-2 text-xs"
          onKeyDown={(e) => {
            if (e.key === "Escape") {
              onClose();
            }
          }}
        />
      </div>

      {/* Type filter buttons */}
      <div className="flex items-center gap-0.5">
        <Button
          variant="ghost"
          size="icon-xs"
          onClick={() => toggleMessageType("user")}
          aria-label="Toggle user messages"
          aria-pressed={messageTypeFilter.has("user")}
          className={messageTypeFilter.has("user") ? "opacity-100" : "opacity-40"}
          title="User messages"
        >
          <User className="h-3 w-3" />
        </Button>
        <Button
          variant="ghost"
          size="icon-xs"
          onClick={() => toggleMessageType("assistant")}
          aria-label="Toggle assistant messages"
          aria-pressed={messageTypeFilter.has("assistant")}
          className={messageTypeFilter.has("assistant") ? "opacity-100" : "opacity-40"}
          title="Assistant messages"
        >
          <Bot className="h-3 w-3" />
        </Button>
        <Button
          variant="ghost"
          size="icon-xs"
          onClick={() => toggleMessageType("tool")}
          aria-label="Toggle tool messages"
          aria-pressed={messageTypeFilter.has("tool")}
          className={messageTypeFilter.has("tool") ? "opacity-100" : "opacity-40"}
          title="Tool call messages"
        >
          <Wrench className="h-3 w-3" />
        </Button>
      </div>

      {/* Agent filter dropdown */}
      {agents && agents.length > 0 && (
        <DropdownMenu>
          <DropdownMenuTrigger asChild>
            <Button variant="ghost" size="xs" className="gap-1 h-7 text-xs max-w-32 truncate">
              <span className="truncate">
                {agentFilter ?? "All agents"}
              </span>
              <ChevronDown className="h-3 w-3 shrink-0" />
            </Button>
          </DropdownMenuTrigger>
          <DropdownMenuContent align="end">
            <DropdownMenuItem onSelect={() => setAgentFilter(null)}>
              All agents
            </DropdownMenuItem>
            {agents
              .filter((a) => !a.hidden)
              .map((agent) => (
                <DropdownMenuItem
                  key={agent.name}
                  onSelect={() => setAgentFilter(agent.name)}
                >
                  {agent.name}
                </DropdownMenuItem>
              ))}
          </DropdownMenuContent>
        </DropdownMenu>
      )}

      {/* Result count */}
      {isFiltering && (
        <span className="text-[10px] text-muted-foreground whitespace-nowrap shrink-0">
          {filteredCount} of {totalCount}
        </span>
      )}

      {/* Clear filters button */}
      {isFiltering && (
        <Button
          variant="ghost"
          size="icon-xs"
          onClick={clearFilters}
          aria-label="Clear filters"
          title="Clear filters"
        >
          <X className="h-3 w-3" />
        </Button>
      )}
    </div>
  );
}
