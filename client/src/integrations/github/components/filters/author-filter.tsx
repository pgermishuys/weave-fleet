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
import { Check, User, Loader2 } from "lucide-react";
import { cn } from "@/lib/utils";
import type { GitHubAssignee } from "../../types";

interface AuthorFilterProps {
  /** Assignee list reused as proxy for likely authors */
  users: GitHubAssignee[];
  isLoading: boolean;
  selected: string | null;
  onSelect: (author: string | null) => void;
}

export function AuthorFilter({
  users,
  isLoading,
  selected,
  onSelect,
}: AuthorFilterProps) {
  const [open, setOpen] = useState(false);

  return (
    <Popover open={open} onOpenChange={setOpen}>
      <PopoverTrigger asChild>
        <Button variant="ghost" size="sm" className="gap-1.5 h-7 text-xs">
          <User className="h-3 w-3" />
          Author
          {selected && (
            <Badge variant="secondary" className="text-[10px] ml-0.5 px-1 py-0">
              1
            </Badge>
          )}
        </Button>
      </PopoverTrigger>
      <PopoverContent className="w-56 p-0" align="start">
        <Command>
          <CommandInput placeholder="Filter authors…" />
          <CommandList className="thin-scrollbar max-h-56">
            {isLoading && users.length === 0 && (
              <div className="flex justify-center py-3">
                <Loader2 className="h-3.5 w-3.5 animate-spin text-muted-foreground" />
              </div>
            )}
            {!isLoading && users.length === 0 && (
              <CommandEmpty>No users found.</CommandEmpty>
            )}
            <CommandGroup>
              {/* Clear option */}
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
              {users.map((user) => {
                const isSelected = selected === user.login;
                return (
                  <CommandItem
                    key={user.login}
                    value={user.login}
                    onSelect={() => {
                      onSelect(isSelected ? null : user.login);
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
                      {/* eslint-disable-next-line @next/next/no-img-element */}
                      <img
                        src={user.avatar_url}
                        alt=""
                        className="h-4 w-4 rounded-full shrink-0"
                      />
                      <span className="text-sm truncate">{user.login}</span>
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
