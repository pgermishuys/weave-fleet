"use client";

import { useState, type ReactNode } from "react";
import Link from "next/link";
import { Loader2, Lock, Star } from "lucide-react";
import {
  Dialog,
  DialogContent,
  DialogHeader,
  DialogTitle,
  DialogTrigger,
} from "@/components/ui/dialog";
import {
  Command,
  CommandEmpty,
  CommandGroup,
  CommandInput,
  CommandItem,
  CommandList,
} from "@/components/ui/command";
import { Badge } from "@/components/ui/badge";
import { useGitHubRepos } from "../hooks/use-github-repos";
import { useBookmarkedRepos } from "../hooks/use-bookmarked-repos";
import { useIntegrationsContext } from "@/contexts/integrations-context";

interface AddRepoDialogProps {
  trigger: ReactNode;
}

export function AddRepoDialog({ trigger }: AddRepoDialogProps) {
  const [open, setOpen] = useState(false);
  const { repos, isLoading, error } = useGitHubRepos();
  const { addRepo, hasRepo } = useBookmarkedRepos();
  const { connectedIntegrations } = useIntegrationsContext();

  const isGitHubConnected = connectedIntegrations.some((i) => i.id === "github");

  const availableRepos = repos.filter((r) => !hasRepo(r.full_name));

  function handleSelect(fullName: string) {
    const repo = repos.find((r) => r.full_name === fullName);
    if (!repo) return;
    addRepo({
      fullName: repo.full_name,
      owner: repo.owner_login,
      name: repo.name,
    });
    setOpen(false);
  }

  return (
    <Dialog open={open} onOpenChange={setOpen}>
      <DialogTrigger asChild>{trigger}</DialogTrigger>
      <DialogContent className="p-0 gap-0 sm:max-w-md top-[10%] translate-y-0" showCloseButton={false}>
        <DialogHeader className="px-4 pt-4 pb-2">
          <DialogTitle>Add Repository</DialogTitle>
        </DialogHeader>

        {!isGitHubConnected ? (
          <div className="px-4 pb-4 text-sm text-muted-foreground space-y-2">
            <p>GitHub is not connected.</p>
            <p>
              Go to{" "}
              <Link
                href="/settings?tab=integrations"
                className="underline hover:text-foreground"
                onClick={() => setOpen(false)}
              >
                Settings &gt; Integrations
              </Link>{" "}
              to connect.
            </p>
          </div>
        ) : (
          <Command>
            <CommandInput placeholder="Search repositories…" />
            <CommandList className="max-h-72 thin-scrollbar">
              {error && (
                <div className="py-3 px-4 text-xs text-destructive">{error}</div>
              )}
              {!error && availableRepos.length === 0 && !isLoading && (
                <CommandEmpty>No repositories found.</CommandEmpty>
              )}
              <CommandGroup>
                {availableRepos.map((repo) => (
                  <CommandItem
                    key={repo.id}
                    value={repo.full_name}
                    onSelect={handleSelect}
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
                          <Badge
                            variant="secondary"
                            className="text-[10px] px-1 py-0"
                          >
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
                {isLoading && availableRepos.length === 0 && (
                  <div className="flex justify-center py-3">
                    <Loader2 className="h-4 w-4 animate-spin text-muted-foreground" />
                  </div>
                )}
              </CommandGroup>
            </CommandList>
          </Command>
        )}
      </DialogContent>
    </Dialog>
  );
}
