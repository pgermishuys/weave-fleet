import { describe, expect, it } from "vitest";
import {
  ANALYTICS_SECTIONS,
  analyticsSectionFromPath,
  analyticsSectionPath,
  isAnalyticsSectionId,
} from "@/lib/analytics-sections";

describe("analytics sections", () => {
  it("gives every section its own route, Overview being the bare /analytics", () => {
    expect(ANALYTICS_SECTIONS.map((section) => analyticsSectionPath(section.id))).toEqual([
      "/analytics",
      "/analytics/daily",
      "/analytics/projects",
      "/analytics/models",
      "/analytics/sessions",
    ]);
  });

  it("reads the section back from its route, trailing slash included", () => {
    for (const section of ANALYTICS_SECTIONS) {
      expect(analyticsSectionFromPath(analyticsSectionPath(section.id))).toBe(section.id);
      expect(analyticsSectionFromPath(`${analyticsSectionPath(section.id)}/`)).toBe(section.id);
    }
  });

  it("opens Overview for an address that isn't a section", () => {
    expect(analyticsSectionFromPath("/analytics/harness")).toBe("overview");
    expect(analyticsSectionFromPath("/analytics/")).toBe("overview");
    expect(isAnalyticsSectionId("harness")).toBe(false);
  });

  it("groups Spend (Overview, Daily) apart from Breakdown (Projects, Models, Sessions)", () => {
    expect(ANALYTICS_SECTIONS.filter((section) => section.group === "spend").map((section) => section.id)).toEqual(["overview", "daily"]);
    expect(ANALYTICS_SECTIONS.filter((section) => section.group === "breakdown").map((section) => section.id)).toEqual(["projects", "models", "sessions"]);
  });
});
