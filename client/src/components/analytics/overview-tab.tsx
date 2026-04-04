"use client";

import {
  AreaChart,
  Area,
  XAxis,
  YAxis,
  CartesianGrid,
  Tooltip,
  ResponsiveContainer,
  Legend,
} from "recharts";
import { Hash, DollarSign, Monitor, MessageSquare } from "lucide-react";
import type { AnalyticsSummary, DailyAnalytics } from "@/lib/api-types";
import { StatCard } from "@/components/analytics/stat-card";
import { formatLargeNumber, formatAnalyticsCost, formatShortDate } from "@/lib/format-utils";
import { CHART_COLORS } from "@/lib/chart-theme";

interface OverviewTabProps {
  summary: AnalyticsSummary | null;
  daily: DailyAnalytics[];
  isLoading: boolean;
}

function isEmpty(summary: AnalyticsSummary | null): boolean {
  if (!summary) return true;
  return (
    summary.totalTokens === 0 &&
    summary.sessionCount === 0 &&
    summary.messageCount === 0
  );
}

export function OverviewTab({ summary, daily, isLoading }: OverviewTabProps) {
  if (!isLoading && isEmpty(summary)) {
    return (
      <div className="flex flex-col items-center justify-center py-16 text-center text-muted-foreground">
        <p className="text-sm">No analytics data yet.</p>
        <p className="text-xs mt-1">Data will appear once sessions generate token usage.</p>
      </div>
    );
  }

  const totalTokens = summary?.totalTokens ?? 0;
  const totalCost = summary?.totalCost ?? 0;
  const estimatedCost = summary?.totalEstimatedCost ?? 0;
  const sessionCount = summary?.sessionCount ?? 0;
  const messageCount = summary?.messageCount ?? 0;
  const topModels = summary?.topModels ?? [];
  const topProjects = summary?.topProjects ?? [];

  // Prepare chart data
  const chartData = daily.map((d) => ({
    date: formatShortDate(d.date),
    tokens: d.tokens,
    cost: d.cost,
  }));

  const maxModel = topModels.reduce((m, p) => Math.max(m, p.cost), 0) || 1;
  const maxProject = topProjects.reduce((m, p) => Math.max(m, p.cost), 0) || 1;

  return (
    <div className="space-y-4 sm:space-y-6">
      {/* Stat cards */}
      <div className="grid grid-cols-2 sm:grid-cols-4 gap-3 sm:gap-4">
        <StatCard
          icon={Hash}
          iconColor="text-purple-500"
          label="Total Tokens"
          value={formatLargeNumber(totalTokens)}
        />
        <StatCard
          icon={DollarSign}
          iconColor="text-green-500"
          label="Total Cost"
          value={formatAnalyticsCost(totalCost)}
          secondaryValue={estimatedCost > 0 ? `est. ${formatAnalyticsCost(estimatedCost)}` : undefined}
        />
        <StatCard
          icon={Monitor}
          iconColor="text-blue-500"
          label="Sessions"
          value={sessionCount.toLocaleString()}
        />
        <StatCard
          icon={MessageSquare}
          iconColor="text-orange-500"
          label="Messages"
          value={messageCount.toLocaleString()}
        />
      </div>

      {/* Daily usage chart */}
      {chartData.length > 0 && (
        <div className="rounded-lg border border-border bg-card p-4">
          <h3 className="text-sm font-semibold mb-3">Daily Usage</h3>
          <div className="h-52">
            <ResponsiveContainer width="100%" height="100%">
              <AreaChart data={chartData} margin={{ top: 4, right: 16, left: 0, bottom: 0 }}>
                <defs>
                  <linearGradient id="colorTokens" x1="0" y1="0" x2="0" y2="1">
                    <stop offset="5%" stopColor={CHART_COLORS[0]} stopOpacity={0.3} />
                    <stop offset="95%" stopColor={CHART_COLORS[0]} stopOpacity={0} />
                  </linearGradient>
                  <linearGradient id="colorCost" x1="0" y1="0" x2="0" y2="1">
                    <stop offset="5%" stopColor={CHART_COLORS[1]} stopOpacity={0.3} />
                    <stop offset="95%" stopColor={CHART_COLORS[1]} stopOpacity={0} />
                  </linearGradient>
                </defs>
                <CartesianGrid strokeDasharray="3 3" stroke="currentColor" strokeOpacity={0.08} />
                <XAxis
                  dataKey="date"
                  tick={{ fontSize: 11, fill: "currentColor", opacity: 0.5 }}
                  tickLine={false}
                  axisLine={false}
                />
                <YAxis
                  yAxisId="tokens"
                  orientation="left"
                  tick={{ fontSize: 11, fill: "currentColor", opacity: 0.5 }}
                  tickLine={false}
                  axisLine={false}
                  tickFormatter={(v: number) => formatLargeNumber(v)}
                  width={48}
                />
                <YAxis
                  yAxisId="cost"
                  orientation="right"
                  tick={{ fontSize: 11, fill: "currentColor", opacity: 0.5 }}
                  tickLine={false}
                  axisLine={false}
                  tickFormatter={(v: number) => `$${v.toFixed(2)}`}
                  width={48}
                />
                <Tooltip
                  contentStyle={{
                    fontSize: 12,
                    borderRadius: 8,
                    border: "1px solid hsl(var(--border))",
                    background: "hsl(var(--popover))",
                    color: "hsl(var(--popover-foreground))",
                  }}
                  formatter={(value, name) => {
                    const num = typeof value === "number" ? value : 0;
                    return name === "tokens"
                      ? [formatLargeNumber(num), "Tokens"]
                      : [formatAnalyticsCost(num), "Cost"];
                  }}
                />
                <Legend
                  wrapperStyle={{ fontSize: 12, paddingTop: 8 }}
                  formatter={(v) => (v === "tokens" ? "Tokens" : "Cost")}
                />
                <Area
                  yAxisId="tokens"
                  type="monotone"
                  dataKey="tokens"
                  stroke={CHART_COLORS[0]}
                  strokeWidth={2}
                  fill="url(#colorTokens)"
                />
                <Area
                  yAxisId="cost"
                  type="monotone"
                  dataKey="cost"
                  stroke={CHART_COLORS[1]}
                  strokeWidth={2}
                  fill="url(#colorCost)"
                />
              </AreaChart>
            </ResponsiveContainer>
          </div>
        </div>
      )}

      {/* Top models + top projects */}
      {(topModels.length > 0 || topProjects.length > 0) && (
        <div className="grid grid-cols-1 sm:grid-cols-2 gap-3 sm:gap-4">
          {/* Top Models */}
          {topModels.length > 0 && (
            <div className="rounded-lg border border-border bg-card p-4">
              <h3 className="text-sm font-semibold mb-3">Top Models</h3>
              <div className="space-y-2">
                {topModels.map((m) => (
                  <div key={m.name} className="space-y-1">
                    <div className="flex items-center justify-between text-xs">
                      <span className="truncate text-foreground font-medium">{m.name}</span>
                      <span className="text-muted-foreground ml-2 shrink-0">{formatAnalyticsCost(m.cost)}</span>
                    </div>
                    <div className="h-1.5 w-full rounded-full bg-muted overflow-hidden">
                      <div
                        className="h-full rounded-full bg-purple-500"
                        style={{ width: `${Math.max(2, (m.cost / maxModel) * 100).toFixed(1)}%` }}
                      />
                    </div>
                  </div>
                ))}
              </div>
            </div>
          )}

          {/* Top Projects */}
          {topProjects.length > 0 && (
            <div className="rounded-lg border border-border bg-card p-4">
              <h3 className="text-sm font-semibold mb-3">Top Projects</h3>
              <div className="space-y-2">
                {topProjects.map((p) => (
                  <div key={p.name} className="space-y-1">
                    <div className="flex items-center justify-between text-xs">
                      <span className="truncate text-foreground font-medium">{p.name}</span>
                      <span className="text-muted-foreground ml-2 shrink-0">{formatAnalyticsCost(p.cost)}</span>
                    </div>
                    <div className="h-1.5 w-full rounded-full bg-muted overflow-hidden">
                      <div
                        className="h-full rounded-full bg-green-500"
                        style={{ width: `${Math.max(2, (p.cost / maxProject) * 100).toFixed(1)}%` }}
                      />
                    </div>
                  </div>
                ))}
              </div>
            </div>
          )}
        </div>
      )}
    </div>
  );
}
