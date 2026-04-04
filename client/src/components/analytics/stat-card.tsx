"use client";

import { cn } from "@/lib/utils";

export interface StatCardProps {
  icon: React.ComponentType<{ className?: string }>;
  iconColor: string;    // Tailwind text color class
  label: string;
  value: string;
  secondaryValue?: string;  // e.g. "estimated: $12.34"
}

export function StatCard({
  icon: Icon,
  iconColor,
  label,
  value,
  secondaryValue,
}: StatCardProps) {
  return (
    <div className="flex flex-col items-center justify-center gap-1.5 rounded-lg border border-border bg-card p-4 text-center shadow-sm">
      <Icon className={cn("h-5 w-5 shrink-0", iconColor)} />
      <p className="text-xl font-bold tabular-nums leading-none">{value}</p>
      <p className="text-xs text-muted-foreground leading-tight">{label}</p>
      {secondaryValue && (
        <p className="text-[11px] text-muted-foreground/70 leading-tight">{secondaryValue}</p>
      )}
    </div>
  );
}
