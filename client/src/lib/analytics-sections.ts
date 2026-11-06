/** The views of Analytics, as the analytics-menu lists them. Each has its own route, so it can be linked to. */
export type AnalyticsSectionId = "overview" | "daily" | "projects" | "models" | "sessions";

export interface AnalyticsSection {
  id: AnalyticsSectionId;
  label: string;
  group: "spend" | "breakdown";
}

export const ANALYTICS_SECTIONS: readonly AnalyticsSection[] = [
  { id: "overview", label: "Overview", group: "spend" },
  { id: "daily", label: "Daily", group: "spend" },
  { id: "projects", label: "Projects", group: "breakdown" },
  { id: "models", label: "Models", group: "breakdown" },
  { id: "sessions", label: "Sessions", group: "breakdown" },
];

export const ANALYTICS_GROUPS: readonly { id: AnalyticsSection["group"]; label: string }[] = [
  { id: "spend", label: "Spend" },
  { id: "breakdown", label: "Breakdown" },
];

export function isAnalyticsSectionId(value: string): value is AnalyticsSectionId {
  return ANALYTICS_SECTIONS.some((section) => section.id === value);
}

/** The route of a section; Overview is the bare /analytics. */
export function analyticsSectionPath(id: AnalyticsSectionId): string {
  return id === "overview" ? "/analytics" : `/analytics/${id}`;
}

/** The section an address shows; anything that isn't a section (a stale link) shows Overview. */
export function analyticsSectionFromPath(pathname: string): AnalyticsSectionId {
  const segment = pathname.replace(/\/+$/, "").split("/")[2] ?? "";
  return isAnalyticsSectionId(segment) ? segment : "overview";
}
