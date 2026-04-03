"use client";

import { useCallback } from "react";
import { Button } from "@/components/ui/button";
import { CircleDot, CircleCheck } from "lucide-react";
import { FilterExpressionField } from "./filter-expression-field";
import { LabelFilter } from "./filters/label-filter";
import { AuthorFilter } from "./filters/author-filter";
import { MilestoneFilter } from "./filters/milestone-filter";
import { AssigneeFilter } from "./filters/assignee-filter";
import { SortControl } from "./filters/sort-control";
import type { IssueFilterState, GitHubLabel, GitHubMilestone, GitHubAssignee } from "../types";

interface IssueFilterBarProps {
  filter: IssueFilterState;
  onChange: (filter: IssueFilterState) => void;
  isSearching?: boolean;
  labels: GitHubLabel[];
  labelsLoading: boolean;
  milestones: GitHubMilestone[];
  milestonesLoading: boolean;
  assignees: GitHubAssignee[];
  assigneesLoading: boolean;
}

export function IssueFilterBar({
  filter,
  onChange,
  isSearching,
  labels,
  labelsLoading,
  milestones,
  milestonesLoading,
  assignees,
  assigneesLoading,
}: IssueFilterBarProps) {
  const setFilter = useCallback(
    (partial: Partial<IssueFilterState>) => {
      onChange({ ...filter, ...partial });
    },
    [filter, onChange]
  );

  const handleLabelToggle = useCallback(
    (label: string) => {
      const next = filter.labels.includes(label)
        ? filter.labels.filter((l) => l !== label)
        : [...filter.labels, label];
      setFilter({ labels: next });
    },
    [filter.labels, setFilter]
  );

  const handleSortChange = useCallback(
    (sort: "created" | "updated" | "comments", direction: "asc" | "desc") => {
      setFilter({ sort, direction });
    },
    [setFilter]
  );

  return (
    <div className="space-y-2">
      {/* Expression field — full width */}
      <FilterExpressionField
        filter={filter}
        onChange={onChange}
        isSearching={isSearching}
      />

      {/* Filter controls row */}
      <div className="flex items-center gap-1 flex-wrap">
        {/* State toggle */}
        <Button
          variant={filter.state === "open" ? "default" : "ghost"}
          size="sm"
          className="gap-1 h-7 text-xs"
          onClick={() => setFilter({ state: "open" })}
        >
          <CircleDot className="h-3 w-3" />
          Open
        </Button>
        <Button
          variant={filter.state === "closed" ? "default" : "ghost"}
          size="sm"
          className="gap-1 h-7 text-xs"
          onClick={() => setFilter({ state: "closed" })}
        >
          <CircleCheck className="h-3 w-3" />
          Closed
        </Button>

        {/* Separator */}
        <div className="h-4 w-px bg-border mx-1" />

        {/* Filter dropdowns */}
        <LabelFilter
          labels={labels}
          isLoading={labelsLoading}
          selected={filter.labels}
          onToggle={handleLabelToggle}
        />
        <AuthorFilter
          users={assignees}
          isLoading={assigneesLoading}
          selected={filter.author}
          onSelect={(author) => setFilter({ author })}
        />
        <MilestoneFilter
          milestones={milestones}
          isLoading={milestonesLoading}
          selected={filter.milestone}
          onSelect={(milestone) => setFilter({ milestone })}
        />
        <AssigneeFilter
          assignees={assignees}
          isLoading={assigneesLoading}
          selected={filter.assignee}
          onSelect={(assignee) => setFilter({ assignee })}
        />

        {/* Sort — push to right */}
        <div className="ml-auto">
          <SortControl
            sort={filter.sort}
            direction={filter.direction}
            onChange={handleSortChange}
          />
        </div>
      </div>
    </div>
  );
}
