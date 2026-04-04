"use client";

import type { AnalyticsTopItem } from "@/lib/api-types";
import { formatLargeNumber, formatAnalyticsCost } from "@/lib/format-utils";

interface ProjectsTabProps {
  projects: AnalyticsTopItem[];
  isLoading: boolean;
}

export function ProjectsTab({ projects, isLoading }: ProjectsTabProps) {
  if (!isLoading && projects.length === 0) {
    return (
      <div className="flex flex-col items-center justify-center py-16 text-center text-muted-foreground">
        <p className="text-sm">No project data yet.</p>
        <p className="text-xs mt-1">Assign sessions to projects to see per-project analytics.</p>
      </div>
    );
  }

  const maxCost = projects.reduce((m, p) => Math.max(m, p.cost), 0) || 1;

  return (
    <div className="grid gap-3 sm:gap-4 sm:grid-cols-2 lg:grid-cols-3">
      {projects.map((project) => (
        <div
          key={project.name}
          className="rounded-lg border border-border bg-card p-4 flex flex-col gap-3"
        >
          <div className="font-semibold text-sm truncate">{project.name}</div>
          <div className="flex items-center justify-between text-sm">
            <span className="text-muted-foreground text-xs">Tokens</span>
            <span className="tabular-nums font-medium">{formatLargeNumber(project.tokens)}</span>
          </div>
          <div className="flex items-center justify-between text-sm">
            <span className="text-muted-foreground text-xs">Cost</span>
            <span className="tabular-nums font-medium">{formatAnalyticsCost(project.cost)}</span>
          </div>
          {/* Proportional cost bar */}
          <div className="space-y-1">
            <div className="h-1.5 w-full rounded-full bg-muted overflow-hidden">
              <div
                className="h-full rounded-full bg-green-500"
                style={{ width: `${Math.max(2, (project.cost / maxCost) * 100).toFixed(1)}%` }}
              />
            </div>
            <p className="text-[10px] text-muted-foreground/60 text-right">
              {((project.cost / maxCost) * 100).toFixed(0)}% of top project
            </p>
          </div>
        </div>
      ))}
    </div>
  );
}
