"use client";

import { Input } from "@/components/ui/input";
import { Button } from "@/components/ui/button";
import {
  Select,
  SelectContent,
  SelectItem,
  SelectTrigger,
  SelectValue,
} from "@/components/ui/select";

export interface AnalyticsDateFilterProps {
  from: string;
  to: string;
  projectId: string;
  projects: Array<{ id: string; name: string }>;
  onFromChange: (date: string) => void;
  onToChange: (date: string) => void;
  onProjectChange: (projectId: string) => void;
  onReset: () => void;
}

export function AnalyticsDateFilter({
  from,
  to,
  projectId,
  projects,
  onFromChange,
  onToChange,
  onProjectChange,
  onReset,
}: AnalyticsDateFilterProps) {
  return (
    <div className="flex flex-wrap items-center gap-2 sm:gap-3 pb-3 sm:pb-4 border-b border-border">
      <div className="flex items-center gap-1.5">
        <span className="text-xs text-muted-foreground font-medium whitespace-nowrap">From</span>
        <Input
          type="date"
          className="w-36 h-8 text-sm"
          value={from}
          onChange={(e) => onFromChange(e.target.value)}
        />
      </div>
      <div className="flex items-center gap-1.5">
        <span className="text-xs text-muted-foreground font-medium whitespace-nowrap">To</span>
        <Input
          type="date"
          className="w-36 h-8 text-sm"
          value={to}
          onChange={(e) => onToChange(e.target.value)}
        />
      </div>
      <Select value={projectId || "__all__"} onValueChange={(v) => onProjectChange(v === "__all__" ? "" : v)}>
        <SelectTrigger className="h-8 w-44 text-sm">
          <SelectValue placeholder="All Projects" />
        </SelectTrigger>
        <SelectContent>
          <SelectItem value="__all__">All Projects</SelectItem>
          {projects.map((p) => (
            <SelectItem key={p.id} value={p.id}>
              {p.name}
            </SelectItem>
          ))}
        </SelectContent>
      </Select>
      <Button variant="ghost" size="sm" onClick={onReset} className="h-8 text-xs text-muted-foreground">
        Reset
      </Button>
    </div>
  );
}
