"use client";

import { useState } from "react";
import { Button } from "@/components/ui/button";
import { Badge } from "@/components/ui/badge";
import { Loader2 } from "lucide-react";
import { useGitHubPulls } from "../hooks/use-github-pulls";
import { PrRow } from "./pr-row";

interface PrListProps {
  owner: string;
  repo: string;
}

type StateFilter = "open" | "closed";

export function PrList({ owner, repo }: PrListProps) {
  const [stateFilter, setStateFilter] = useState<StateFilter>("open");

  const { pulls, isLoading, error, hasMore, loadMore, refetch } =
    useGitHubPulls(owner, repo, { state: stateFilter });

  return (
    <div className="space-y-3">
      {/* Filter bar */}
      <div className="flex items-center gap-2">
        <Button
          variant={stateFilter === "open" ? "default" : "ghost"}
          size="sm"
          className="gap-1.5 h-7"
          onClick={() => setStateFilter("open")}
        >
          Open
        </Button>
        <Button
          variant={stateFilter === "closed" ? "default" : "ghost"}
          size="sm"
          className="gap-1.5 h-7"
          onClick={() => setStateFilter("closed")}
        >
          Closed
        </Button>
        <Button
          variant="ghost"
          size="sm"
          className="h-7 ml-auto text-xs"
          onClick={refetch}
          disabled={isLoading}
        >
          {isLoading ? <Loader2 className="h-3.5 w-3.5 animate-spin" /> : "Refresh"}
        </Button>
      </div>

      {error && (
        <div className="px-3 py-2 text-xs text-destructive rounded-md border border-destructive/20 bg-destructive/10">
          {error}
        </div>
      )}

      {!error && pulls.length === 0 && !isLoading && (
        <div className="flex flex-col items-center justify-center py-12 gap-2">
          <p className="text-sm text-muted-foreground">
            No {stateFilter} pull requests found.
          </p>
        </div>
      )}

      <div className="space-y-1">
        {pulls.map((pr) => (
          <PrRow key={pr.id} pr={pr} owner={owner} repo={repo} />
        ))}
      </div>

      {isLoading && (
        <div className="flex items-center justify-center py-4">
          <Loader2 className="h-4 w-4 animate-spin text-muted-foreground" />
        </div>
      )}

      {!isLoading && hasMore && (
        <div className="flex justify-center pt-2">
          <Button variant="outline" size="sm" onClick={loadMore}>
            Load more
            <Badge variant="secondary" className="ml-1 text-[10px]">
              +30
            </Badge>
          </Button>
        </div>
      )}
    </div>
  );
}
