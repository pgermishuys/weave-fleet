"use client";

import { useState } from "react";
import { Button } from "@/components/ui/button";
import { Badge } from "@/components/ui/badge";
import {
  Popover,
  PopoverContent,
  PopoverTrigger,
} from "@/components/ui/popover";
import {
  Command,
  CommandEmpty,
  CommandGroup,
  CommandInput,
  CommandItem,
  CommandList,
} from "@/components/ui/command";
import { Check, Milestone, Loader2 } from "lucide-react";
import { cn } from "@/lib/utils";
import type { GitHubMilestone } from "../../types";

interface MilestoneFilterProps {
  milestones: GitHubMilestone[];
  isLoading: boolean;
  selected: string | null;
  onSelect: (milestone: string | null) => void;
}

export function MilestoneFilter({
  milestones,
  isLoading,
  selected,
  onSelect,
}: MilestoneFilterProps) {
  const [open, setOpen] = useState(false);

  return (
    <Popover open={open} onOpenChange={setOpen}>
      <PopoverTrigger asChild>
        <Button variant="ghost" size="sm" className="gap-1.5 h-7 text-xs">
          <Milestone className="h-3 w-3" />
          Milestone
          {selected && (
            <Badge variant="secondary" className="text-[10px] ml-0.5 px-1 py-0">
              1
            </Badge>
          )}
        </Button>
      </PopoverTrigger>
      <PopoverContent className="w-56 p-0" align="start">
        <Command>
          <CommandInput placeholder="Filter milestones…" />
          <CommandList className="thin-scrollbar max-h-56">
            {isLoading && milestones.length === 0 && (
              <div className="flex justify-center py-3">
                <Loader2 className="h-3.5 w-3.5 animate-spin text-muted-foreground" />
              </div>
            )}
            {!isLoading && milestones.length === 0 && (
              <CommandEmpty>No milestones found.</CommandEmpty>
            )}
            <CommandGroup>
              {/* Clear / special options */}
              {selected && (
                <CommandItem
                  value="__clear__"
                  onSelect={() => {
                    onSelect(null);
                    setOpen(false);
                  }}
                >
                  <span className="text-sm text-muted-foreground">Clear selection</span>
                </CommandItem>
              )}
              <CommandItem
                value="__none__"
                onSelect={() => {
                  onSelect(selected === "none" ? null : "none");
                  setOpen(false);
                }}
              >
                <div className="flex items-center gap-2">
                  <Check
                    className={cn(
                      "h-3.5 w-3.5 shrink-0",
                      selected === "none" ? "opacity-100" : "opacity-0"
                    )}
                  />
                  <span className="text-sm">No milestone</span>
                </div>
              </CommandItem>

              {milestones.map((ms) => {
                const isSelected = selected === ms.title;
                return (
                  <CommandItem
                    key={ms.number}
                    value={ms.title}
                    onSelect={() => {
                      onSelect(isSelected ? null : ms.title);
                      setOpen(false);
                    }}
                  >
                    <div className="flex items-center gap-2 flex-1 min-w-0">
                      <Check
                        className={cn(
                          "h-3.5 w-3.5 shrink-0",
                          isSelected ? "opacity-100" : "opacity-0"
                        )}
                      />
                      <div className="flex flex-col min-w-0">
                        <span className="text-sm truncate">{ms.title}</span>
                        <span className="text-[10px] text-muted-foreground">
                          {ms.open_issues} open · {ms.closed_issues} closed
                        </span>
                      </div>
                    </div>
                  </CommandItem>
                );
              })}
            </CommandGroup>
          </CommandList>
        </Command>
      </PopoverContent>
    </Popover>
  );
}
