"use client";

import {
  BarChart,
  Bar,
  XAxis,
  YAxis,
  CartesianGrid,
  Tooltip,
  ResponsiveContainer,
} from "recharts";
import type { ModelAnalytics } from "@/lib/api-types";
import { formatLargeNumber, formatAnalyticsCost } from "@/lib/format-utils";
import { CHART_COLORS } from "@/lib/chart-theme";

interface ModelsTabProps {
  models: ModelAnalytics[];
  isLoading: boolean;
}

export function ModelsTab({ models, isLoading }: ModelsTabProps) {
  if (!isLoading && models.length === 0) {
    return (
      <div className="flex flex-col items-center justify-center py-16 text-center text-muted-foreground">
        <p className="text-sm">No model data yet.</p>
        <p className="text-xs mt-1">Model analytics will appear once sessions use AI models.</p>
      </div>
    );
  }

  // Sort by cost descending for chart
  const sorted = [...models].sort((a, b) => b.cost - a.cost);

  const chartData = sorted.map((m) => ({
    name: m.modelId.split("/").pop() ?? m.modelId,
    cost: m.cost,
    tokens: m.tokens,
  }));

  return (
    <div className="space-y-4 sm:space-y-6">
      {/* Horizontal bar chart */}
      {chartData.length > 0 && (
        <div className="rounded-lg border border-border bg-card p-4">
          <h3 className="text-sm font-semibold mb-3">Cost by Model</h3>
          <div className="h-48">
            <ResponsiveContainer width="100%" height="100%">
              <BarChart
                data={chartData}
                layout="vertical"
                margin={{ top: 0, right: 60, left: 0, bottom: 0 }}
              >
                <CartesianGrid strokeDasharray="3 3" stroke="currentColor" strokeOpacity={0.08} horizontal={false} />
                <XAxis
                  type="number"
                  tick={{ fontSize: 11, fill: "currentColor", opacity: 0.5 }}
                  tickLine={false}
                  axisLine={false}
                  tickFormatter={(v: number) => `$${v.toFixed(2)}`}
                />
                <YAxis
                  type="category"
                  dataKey="name"
                  tick={{ fontSize: 11, fill: "currentColor", opacity: 0.7 }}
                  tickLine={false}
                  axisLine={false}
                  width={120}
                />
                <Tooltip
                  contentStyle={{
                    fontSize: 12,
                    borderRadius: 8,
                    border: "1px solid hsl(var(--border))",
                    background: "hsl(var(--popover))",
                    color: "hsl(var(--popover-foreground))",
                  }}
                  formatter={(value) => {
                    const num = typeof value === "number" ? value : 0;
                    return [formatAnalyticsCost(num), "Cost"];
                  }}
                />
                <Bar dataKey="cost" fill={CHART_COLORS[0]} radius={[0, 4, 4, 0]} />
              </BarChart>
            </ResponsiveContainer>
          </div>
        </div>
      )}

      {/* Detail table */}
      <div className="overflow-x-auto rounded-lg border border-border">
        <table className="w-full text-sm">
          <thead className="border-b border-border bg-muted/30">
            <tr>
              <th className="px-3 py-2 text-left text-xs font-semibold uppercase tracking-wider text-muted-foreground">Model</th>
              <th className="px-3 py-2 text-left text-xs font-semibold uppercase tracking-wider text-muted-foreground hidden sm:table-cell">Provider</th>
              <th className="px-3 py-2 text-right text-xs font-semibold uppercase tracking-wider text-muted-foreground">Tokens</th>
              <th className="px-3 py-2 text-right text-xs font-semibold uppercase tracking-wider text-muted-foreground">Cost</th>
              <th className="px-3 py-2 text-right text-xs font-semibold uppercase tracking-wider text-muted-foreground hidden sm:table-cell">Messages</th>
              <th className="px-3 py-2 text-right text-xs font-semibold uppercase tracking-wider text-muted-foreground hidden sm:table-cell">Avg/Msg</th>
            </tr>
          </thead>
          <tbody>
            {sorted.map((model) => (
              <tr
                key={model.modelId}
                className="border-b border-border last:border-0 hover:bg-muted/20 transition-colors even:bg-muted/10"
              >
                <td className="px-3 py-2 font-medium max-w-[160px]">
                  <span className="truncate block">{model.modelId.split("/").pop() ?? model.modelId}</span>
                </td>
                <td className="px-3 py-2 text-muted-foreground hidden sm:table-cell">
                  {model.providerId}
                </td>
                <td className="px-3 py-2 text-right tabular-nums text-muted-foreground">
                  {formatLargeNumber(model.tokens)}
                </td>
                <td className="px-3 py-2 text-right tabular-nums font-medium">
                  {formatAnalyticsCost(model.cost)}
                </td>
                <td className="px-3 py-2 text-right tabular-nums text-muted-foreground hidden sm:table-cell">
                  {model.messageCount.toLocaleString()}
                </td>
                <td className="px-3 py-2 text-right tabular-nums text-muted-foreground hidden sm:table-cell">
                  {formatAnalyticsCost(model.avgCostPerMessage)}
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      </div>
    </div>
  );
}
