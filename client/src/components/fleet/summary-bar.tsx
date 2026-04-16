import { FleetSummary } from "@/lib/types";
import { formatTokens } from "@/lib/format-utils";
import {
  Zap,
  Pause,
  Hash,
  ListTodo,
} from "lucide-react";

interface SummaryBarProps {
  summary: FleetSummary;
}

export function SummaryBar({ summary }: SummaryBarProps) {
  const items = [
    {
      label: "Active",
      value: summary.activeSessions,
      icon: Zap,
      color: "text-green-500",
    },
    {
      label: "Idle",
      value: summary.idleSessions,
      icon: Pause,
      color: "text-muted-foreground",
    },
    {
      label: "Tokens",
      value: formatTokens(summary.totalTokens),
      icon: Hash,
      color: "text-purple-500",
    },
    {
      label: "Queued",
      value: summary.queuedTasks,
      icon: ListTodo,
      color: "text-orange-500",
    },
  ];

  return (
    <div className="grid grid-cols-2 gap-3 sm:grid-cols-4 sm:gap-4" data-testid="summary-bar">
      {items.map((item) => (
        <div
          key={item.label}
          className="relative overflow-hidden flex flex-col items-center rounded-xl border bg-card p-3 sm:p-4 text-center shadow-[var(--card-shadow)] transition-shadow duration-200 hover:shadow-[var(--card-shadow-hover)]"
          data-testid={`summary-${item.label.toLowerCase()}`}
        >
          {/* Gradient top accent */}
          <span className="absolute inset-x-0 top-0 h-0.5 weave-gradient-bg" aria-hidden="true" />
          <item.icon className={`h-5 w-5 ${item.color}`} />
          <span className="mt-1 text-2xl font-semibold tabular-nums tracking-tight" data-testid={`summary-${item.label.toLowerCase()}-count`}>{item.value}</span>
          <span className="text-sm text-muted-foreground">{item.label}</span>
        </div>
      ))}
    </div>
  );
}
