"use client";

/** Format large numbers with abbreviations (e.g. 12345 → "12.3k"). */
function formatTokenCount(count: number): string {
  if (count < 1000) return String(count);
  if (count < 1_000_000) return `${(count / 1000).toFixed(1)}k`;
  return `${(count / 1_000_000).toFixed(1)}M`;
}

/** Format cost with appropriate precision. */
function formatCost(cost: number): string {
  if (cost === 0) return "$0.00";
  if (cost < 0.01) return `$${cost.toFixed(4)}`;
  if (cost < 1) return `$${cost.toFixed(3)}`;
  return `$${cost.toFixed(2)}`;
}

/** Color class for cost tiers. */
function costColorClass(cost: number): string {
  if (cost <= 0) return "text-muted-foreground";
  if (cost < 0.01) return "text-green-500";
  if (cost < 0.10) return "text-yellow-500";
  if (cost < 1.00) return "text-orange-500";
  return "text-red-500";
}

interface TokenCostBreakdownProps {
  tokens: { input: number; output: number; reasoning: number };
  cost: number;
  variant: "sidebar" | "compact";
  /** Override the total token count (used when only aggregate total is available, not per-type breakdown). */
  totalOverride?: number;
}

export function TokenCostBreakdown({ tokens, cost, variant, totalOverride }: TokenCostBreakdownProps) {
  const totalTokens = totalOverride ?? (tokens.input + tokens.output + tokens.reasoning);

  if (variant === "compact") {
    if (totalTokens === 0 && cost === 0) return null;
    return (
      <span className="text-[10px] text-muted-foreground">
        {formatTokenCount(totalTokens)} tokens
        {cost > 0 && (
          <> · <span className={costColorClass(cost)}>{formatCost(cost)}</span></>
        )}
      </span>
    );
  }

  // sidebar variant — full breakdown
  if (totalTokens === 0 && cost === 0) {
    return (
      <p className="text-xs text-muted-foreground italic">
        No token data yet
      </p>
    );
  }
  return (
    <div className="space-y-1.5">
      <div className="flex items-center justify-between">
        <span className="text-xs text-muted-foreground">Total</span>
        <span className="text-xs font-medium">{formatTokenCount(totalTokens)}</span>
      </div>
      <div className="flex items-center justify-between">
        <span className="text-xs text-muted-foreground">Input</span>
        <span className="text-xs text-muted-foreground">{formatTokenCount(tokens.input)}</span>
      </div>
      <div className="flex items-center justify-between">
        <span className="text-xs text-muted-foreground">Output</span>
        <span className="text-xs text-muted-foreground">{formatTokenCount(tokens.output)}</span>
      </div>
      {tokens.reasoning > 0 && (
        <div className="flex items-center justify-between">
          <span className="text-xs text-muted-foreground">Reasoning</span>
          <span className="text-xs text-muted-foreground">{formatTokenCount(tokens.reasoning)}</span>
        </div>
      )}
      {cost > 0 && (
        <div className="flex items-center justify-between pt-1 border-t border-border/40">
          <span className="text-xs text-muted-foreground">Cost</span>
          <span className={`text-xs font-medium ${costColorClass(cost)}`}>{formatCost(cost)}</span>
        </div>
      )}
    </div>
  );
}
