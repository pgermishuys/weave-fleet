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
import { Check, Tag, Loader2 } from "lucide-react";
import { cn } from "@/lib/utils";
import type { GitHubLabel } from "../../types";

interface LabelFilterProps {
  labels: GitHubLabel[];
  isLoading: boolean;
  selected: string[];
  onToggle: (label: string) => void;
}

export function LabelFilter({
  labels,
  isLoading,
  selected,
  onToggle,
}: LabelFilterProps) {
  const [open, setOpen] = useState(false);

  return (
    <Popover open={open} onOpenChange={setOpen}>
      <PopoverTrigger asChild>
        <Button variant="ghost" size="sm" className="gap-1.5 h-7 text-xs">
          <Tag className="h-3 w-3" />
          Label
          {selected.length > 0 && (
            <Badge variant="secondary" className="text-[10px] ml-0.5 px-1 py-0">
              {selected.length}
            </Badge>
          )}
        </Button>
      </PopoverTrigger>
      <PopoverContent className="w-64 p-0" align="start">
        <Command>
          <CommandInput placeholder="Filter labels…" />
          <CommandList className="thin-scrollbar max-h-56">
            {isLoading && labels.length === 0 && (
              <div className="flex justify-center py-3">
                <Loader2 className="h-3.5 w-3.5 animate-spin text-muted-foreground" />
              </div>
            )}
            {!isLoading && labels.length === 0 && (
              <CommandEmpty>No labels found.</CommandEmpty>
            )}
            <CommandGroup>
              {labels.map((label) => {
                const isSelected = selected.includes(label.name);
                return (
                  <CommandItem
                    key={label.name}
                    value={label.name}
                    onSelect={() => onToggle(label.name)}
                  >
                    <div className="flex items-center gap-2 flex-1 min-w-0">
                      <div
                        className={cn(
                          "flex h-4 w-4 shrink-0 items-center justify-center rounded-sm border",
                          isSelected
                            ? "border-primary bg-primary text-primary-foreground"
                            : "border-muted-foreground/30"
                        )}
                      >
                        {isSelected && <Check className="h-3 w-3" />}
                      </div>
                      <span
                        className="h-2.5 w-2.5 rounded-full shrink-0"
                        style={{ backgroundColor: `#${label.color}` }}
                      />
                      <span className="text-sm truncate">{label.name}</span>
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
