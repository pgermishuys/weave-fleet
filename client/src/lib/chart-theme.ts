/**
 * Chart theme utilities — provides Recharts color tokens that adapt to the
 * current CSS variable theme (dark / light / black).
 */

/**
 * Returns computed CSS variable values for Recharts theming.
 * Falls back to sensible defaults for SSR.
 */
export function getChartColors(): {
  primary: string;
  secondary: string;
  accent: string;
  muted: string;
  background: string;
  foreground: string;
} {
  if (typeof document === "undefined") {
    // SSR fallback
    return {
      primary: "hsl(220 70% 50%)",
      secondary: "hsl(160 60% 45%)",
      accent: "hsl(30 80% 55%)",
      muted: "hsl(0 0% 40%)",
      background: "hsl(0 0% 10%)",
      foreground: "hsl(0 0% 90%)",
    };
  }

  const style = getComputedStyle(document.documentElement);
  const get = (v: string, fallback: string) =>
    style.getPropertyValue(v).trim() || fallback;

  return {
    primary: `hsl(${get("--primary", "220 70% 50%")})`,
    secondary: `hsl(${get("--secondary", "220 14% 30%")})`,
    accent: `hsl(${get("--accent", "220 14% 25%")})`,
    muted: `hsl(${get("--muted-foreground", "215 20% 55%")})`,
    background: `hsl(${get("--popover", "220 14% 10%")})`,
    foreground: `hsl(${get("--foreground", "0 0% 95%")})`,
  };
}

/** Palette of distinct colors for multi-series charts (CSS hsl() strings) */
export const CHART_PALETTE = [
  "hsl(var(--chart-1, 220 70% 50%))",
  "hsl(var(--chart-2, 160 60% 45%))",
  "hsl(var(--chart-3, 30 80% 55%))",
  "hsl(var(--chart-4, 280 65% 60%))",
  "hsl(var(--chart-5, 340 75% 55%))",
];

/** Fallback palette using concrete hsl values (safe for Recharts without CSS vars) */
export const CHART_COLORS = [
  "hsl(220, 70%, 50%)",
  "hsl(160, 60%, 45%)",
  "hsl(30, 80%, 55%)",
  "hsl(280, 65%, 60%)",
  "hsl(340, 75%, 55%)",
];
