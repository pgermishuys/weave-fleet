"use client";

import { useState } from "react";
import type { SessionAnalytics } from "@/lib/api-types";
import { formatLargeNumber, formatAnalyticsCost, formatRelativeTime } from "@/lib/format-utils";
import { cn } from "@/lib/utils";
import { ChevronUp, ChevronDown } from "lucide-react";

interface SessionsTabProps {
  sessions: SessionAnalytics[];
  isLoading: boolean;
}

type SortColumn = "title" | "project" | "tokens" | "cost" | "duration" | "createdAt";
type SortDirection = "asc" | "desc";

function formatDuration(seconds: number | null): string {
  if (seconds == null) return "—";
  const m = Math.floor(seconds / 60);
  const s = seconds % 60;
  if (m > 0) return `${m}m ${s}s`;
  return `${s}s`;
}

export function SessionsTab({ sessions, isLoading }: SessionsTabProps) {
  const [sort, setSort] = useState<{ column: SortColumn; direction: SortDirection }>({
    column: "createdAt",
    direction: "desc",
  });

  const toggleSort = (column: SortColumn) => {
    setSort((prev) =>
      prev.column === column
        ? { column, direction: prev.direction === "asc" ? "desc" : "asc" }
        : { column, direction: "desc" }
    );
  };

  const sorted = [...sessions].sort((a, b) => {
    let cmp = 0;
    switch (sort.column) {
      case "title":
        cmp = (a.title ?? "").localeCompare(b.title ?? "");
        break;
      case "project":
        cmp = (a.projectName ?? "").localeCompare(b.projectName ?? "");
        break;
      case "tokens":
        cmp = a.tokens - b.tokens;
        break;
      case "cost":
        cmp = a.cost - b.cost;
        break;
      case "duration":
        cmp = (a.durationSeconds ?? 0) - (b.durationSeconds ?? 0);
        break;
      case "createdAt":
        cmp = new Date(a.createdAt).getTime() - new Date(b.createdAt).getTime();
        break;
    }
    return sort.direction === "asc" ? cmp : -cmp;
  });

  if (!isLoading && sessions.length === 0) {
    return (
      <div className="flex flex-col items-center justify-center py-16 text-center text-muted-foreground">
        <p className="text-sm">No session data yet.</p>
        <p className="text-xs mt-1">Session analytics will appear once sessions are completed.</p>
      </div>
    );
  }

  const SortIcon = ({ column }: { column: SortColumn }) => {
    if (sort.column !== column) return null;
    return sort.direction === "asc" ? (
      <ChevronUp className="inline h-3 w-3 ml-0.5" />
    ) : (
      <ChevronDown className="inline h-3 w-3 ml-0.5" />
    );
  };

  const th = (column: SortColumn, label: string, className?: string) => (
    <th
      className={cn(
        "px-3 py-2 text-left text-xs font-semibold uppercase tracking-wider text-muted-foreground cursor-pointer hover:text-foreground select-none whitespace-nowrap",
        className
      )}
      onClick={() => toggleSort(column)}
    >
      {label}
      <SortIcon column={column} />
    </th>
  );

  return (
    <div className="overflow-x-auto rounded-lg border border-border">
      <table className="w-full text-sm">
        <thead className="border-b border-border bg-muted/30">
          <tr>
            {th("title", "Title")}
            {th("project", "Project")}
            {th("tokens", "Tokens", "text-right")}
            {th("cost", "Cost", "text-right")}
            {th("duration", "Duration", "hidden sm:table-cell text-right")}
            <th className="px-3 py-2 text-left text-xs font-semibold uppercase tracking-wider text-muted-foreground hidden sm:table-cell">
              Models
            </th>
            {th("createdAt", "Created", "text-right")}
          </tr>
        </thead>
        <tbody>
          {sorted.map((session) => (
            <tr
              key={session.sessionId}
              className="border-b border-border last:border-0 hover:bg-muted/20 transition-colors even:bg-muted/10"
            >
              <td className="px-3 py-2 max-w-[180px]">
                <span className="truncate block text-foreground">
                  {session.title ?? <span className="text-muted-foreground italic">Untitled</span>}
                </span>
              </td>
              <td className="px-3 py-2">
                {session.projectName ? (
                  <span className="inline-flex items-center rounded-full border border-border px-2 py-0.5 text-[10px] font-medium text-muted-foreground">
                    {session.projectName}
                  </span>
                ) : (
                  <span className="text-muted-foreground/50 text-xs">—</span>
                )}
              </td>
              <td className="px-3 py-2 text-right tabular-nums text-muted-foreground">
                {formatLargeNumber(session.tokens)}
              </td>
              <td className="px-3 py-2 text-right tabular-nums font-medium">
                {formatAnalyticsCost(session.cost)}
              </td>
              <td className="px-3 py-2 text-right tabular-nums text-muted-foreground hidden sm:table-cell">
                {formatDuration(session.durationSeconds)}
              </td>
              <td className="px-3 py-2 hidden sm:table-cell">
                <div className="flex flex-wrap gap-1">
                  {session.models.slice(0, 2).map((m) => (
                    <span
                      key={m}
                      className="inline-flex items-center rounded-full border border-border px-1.5 py-0.5 text-[9px] font-medium text-muted-foreground"
                    >
                      {m.split("/").pop() ?? m}
                    </span>
                  ))}
                  {session.models.length > 2 && (
                    <span className="text-[9px] text-muted-foreground/60">
                      +{session.models.length - 2}
                    </span>
                  )}
                </div>
              </td>
              <td className="px-3 py-2 text-right tabular-nums text-muted-foreground text-xs whitespace-nowrap">
                {formatRelativeTime(session.createdAt)}
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}
