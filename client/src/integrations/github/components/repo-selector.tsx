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
import { ChevronDown, Star, Lock, RefreshCw, Loader2 } from "lucide-react";
import { useGitHubRepos } from "../hooks/use-github-repos";
import type { CachedGitHubRepo } from "../types";

interface RepoSelectorProps {
  selected: CachedGitHubRepo | null;
  onSelect: (repo: CachedGitHubRepo) => void;
}

export function RepoSelector({ selected, onSelect }: RepoSelectorProps) {
  const [open, setOpen] = useState(false);
  const { repos, isLoading, error, refresh } = useGitHubRepos();

  return (
    <div className="flex items-center gap-2">
      <Popover open={open} onOpenChange={setOpen}>
        <PopoverTrigger asChild>
          <Button variant="outline" size="sm" className="gap-1.5 max-w-xs">
            <span className="truncate">
              {selected ? selected.full_name : "Select a repository…"}
            </span>
            <ChevronDown className="h-3.5 w-3.5 shrink-0" />
          </Button>
        </PopoverTrigger>
        <PopoverContent className="w-80 p-0" align="start">
          <Command>
            <CommandInput placeholder="Search repositories…" />
            <CommandList className="thin-scrollbar">
              {error && (
                <div className="py-3 px-4 text-xs text-destructive">{error}</div>
              )}
              {!error && repos.length === 0 && !isLoading && (
                <CommandEmpty>No repositories found.</CommandEmpty>
              )}
              <CommandGroup>
                {repos.map((repo) => (
                  <CommandItem
                    key={repo.id}
                    value={repo.full_name}
                    onSelect={() => {
                      onSelect(repo);
                      setOpen(false);
                    }}
                  >
                    <div className="flex flex-col gap-0.5 flex-1 min-w-0">
                      <div className="flex items-center gap-1.5">
                        <span className="text-sm truncate">{repo.full_name}</span>
                        {repo.private && (
                          <Lock className="h-3 w-3 shrink-0 text-muted-foreground" />
                        )}
                      </div>
                      <div className="flex items-center gap-2 text-[10px] text-muted-foreground">
                        {repo.language && (
                          <Badge variant="secondary" className="text-[10px] px-1 py-0">
                            {repo.language}
                          </Badge>
                        )}
                        <span className="flex items-center gap-0.5">
                          <Star className="h-2.5 w-2.5" />
                          {repo.stargazers_count}
                        </span>
                      </div>
                    </div>
                  </CommandItem>
                ))}
                {isLoading && repos.length === 0 && (
                  <div className="flex justify-center py-3">
                    <Loader2 className="h-3.5 w-3.5 animate-spin text-muted-foreground" />
                  </div>
                )}
              </CommandGroup>
            </CommandList>
          </Command>
        </PopoverContent>
      </Popover>

      <Button
        variant="ghost"
        size="icon-sm"
        onClick={refresh}
        disabled={isLoading}
        aria-label="Refresh repositories"
      >
        <RefreshCw className={`h-3.5 w-3.5 ${isLoading ? "animate-spin" : ""}`} />
      </Button>
    </div>
  );
}
