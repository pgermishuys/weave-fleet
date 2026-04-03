"use client";

import { useState } from "react";
import { Button } from "@/components/ui/button";
import { Badge } from "@/components/ui/badge";
import { Loader2, RefreshCw } from "lucide-react";
import { useGitHubIssues } from "../hooks/use-github-issues";
import { useGitHubLabels } from "../hooks/use-github-labels";
import { useGitHubMilestones } from "../hooks/use-github-milestones";
import { useGitHubAssignees } from "../hooks/use-github-assignees";
import { IssueFilterBar } from "./issue-filter-bar";
import { IssueRow } from "./issue-row";
import { DEFAULT_ISSUE_FILTER, type IssueFilterState } from "../types";
import { serializeFilterExpression } from "../lib/filter-expression";

interface IssueListProps {
  owner: string;
  repo: string;
}

export function IssueList({ owner, repo }: IssueListProps) {
  const [filter, setFilter] = useState<IssueFilterState>({
    ...DEFAULT_ISSUE_FILTER,
  });

  // Metadata hooks for filter dropdowns
  const { data: labels, isLoading: labelsLoading } = useGitHubLabels(owner, repo);
  const { data: milestones, isLoading: milestonesLoading } = useGitHubMilestones(owner, repo);
  const { data: assignees, isLoading: assigneesLoading } = useGitHubAssignees(owner, repo);

  // Issues with full filter state + milestone title→number resolution
  const { issues, isLoading, isSearching, error, hasMore, loadMore, refetch } =
    useGitHubIssues(owner, repo, filter, milestones);

  const hasActiveFilters = serializeFilterExpression(filter).length > 0;

  return (
    <div className="space-y-3">
      {/* Filter bar */}
      <div className="flex items-center gap-2">
        <div className="flex-1 min-w-0">
          <IssueFilterBar
            filter={filter}
            onChange={setFilter}
            isSearching={isSearching}
            labels={labels}
            labelsLoading={labelsLoading}
            milestones={milestones}
            milestonesLoading={milestonesLoading}
            assignees={assignees}
            assigneesLoading={assigneesLoading}
          />
        </div>
        <Button
          variant="ghost"
          size="icon-sm"
          onClick={refetch}
          disabled={isLoading}
          aria-label="Refresh issues"
          className="shrink-0"
        >
          <RefreshCw className={`h-3.5 w-3.5 ${isLoading ? "animate-spin" : ""}`} />
        </Button>
      </div>

      {error && (
        <div className="px-3 py-2 text-xs text-destructive rounded-md border border-destructive/20 bg-destructive/10">
          {error}
        </div>
      )}

      {!error && issues.length === 0 && !isLoading && (
        <div className="flex flex-col items-center justify-center py-12 gap-2">
          <p className="text-sm text-muted-foreground">
            {hasActiveFilters
              ? "No issues match the current filters."
              : `No ${filter.state === "all" ? "" : filter.state + " "}issues found.`}
          </p>
        </div>
      )}

      <div className="space-y-1">
        {issues.map((issue) => (
          <IssueRow key={issue.id} issue={issue} owner={owner} repo={repo} />
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
