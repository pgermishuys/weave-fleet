/**
 * Unit tests for `useAnalyticsFilters` hook.
 * Tests the filter state management, persistence, and defaults.
 */

import { renderHook, act } from "@testing-library/react";
import { afterEach, describe, it, expect } from "vitest";
import { useAnalyticsFilters } from "@/hooks/use-analytics-filters";

const STORAGE_KEY = "weave:analytics:filters";

function todayISO(): string {
  return new Date().toISOString().split("T")[0];
}

function daysAgoISO(days: number): string {
  const d = new Date();
  d.setDate(d.getDate() - days);
  return d.toISOString().split("T")[0];
}

afterEach(() => {
  localStorage.clear();
});

// ─── Default filters ──────────────────────────────────────────────────────────

describe("useAnalyticsFilters — defaults", () => {
  it("defaults 'to' to today's date", () => {
    const { result } = renderHook(() => useAnalyticsFilters());
    expect(result.current.filters.to).toBe(todayISO());
  });

  it("defaults 'from' to 30 days ago", () => {
    const { result } = renderHook(() => useAnalyticsFilters());
    expect(result.current.filters.from).toBe(daysAgoISO(30));
  });

  it("defaults 'projectId' to empty string (all projects)", () => {
    const { result } = renderHook(() => useAnalyticsFilters());
    expect(result.current.filters.projectId).toBe("");
  });
});

// ─── setFrom / setTo / setProjectId ──────────────────────────────────────────

describe("useAnalyticsFilters — setters", () => {
  it("setFrom updates the from date", () => {
    const { result } = renderHook(() => useAnalyticsFilters());
    act(() => {
      result.current.setFrom("2025-01-01");
    });
    expect(result.current.filters.from).toBe("2025-01-01");
  });

  it("setTo updates the to date", () => {
    const { result } = renderHook(() => useAnalyticsFilters());
    act(() => {
      result.current.setTo("2025-01-31");
    });
    expect(result.current.filters.to).toBe("2025-01-31");
  });

  it("setProjectId updates the projectId", () => {
    const { result } = renderHook(() => useAnalyticsFilters());
    act(() => {
      result.current.setProjectId("proj_abc123");
    });
    expect(result.current.filters.projectId).toBe("proj_abc123");
  });

  it("setters update fields independently (other fields unchanged)", () => {
    const { result } = renderHook(() => useAnalyticsFilters());
    act(() => {
      result.current.setFrom("2025-03-01");
    });
    // to and projectId should remain at their default values
    expect(result.current.filters.to).toBe(todayISO());
    expect(result.current.filters.projectId).toBe("");
  });
});

// ─── resetFilters ─────────────────────────────────────────────────────────────

describe("useAnalyticsFilters — resetFilters", () => {
  it("resetFilters restores default dates and clears projectId", () => {
    const { result } = renderHook(() => useAnalyticsFilters());

    // Set custom values
    act(() => {
      result.current.setFrom("2024-01-01");
      result.current.setTo("2024-01-31");
      result.current.setProjectId("proj_xyz");
    });

    // Now reset
    act(() => {
      result.current.resetFilters();
    });

    expect(result.current.filters.from).toBe(daysAgoISO(30));
    expect(result.current.filters.to).toBe(todayISO());
    expect(result.current.filters.projectId).toBe("");
  });
});

// ─── localStorage persistence ─────────────────────────────────────────────────

describe("useAnalyticsFilters — persistence", () => {
  it("persists filters to localStorage when setFrom is called", () => {
    const { result } = renderHook(() => useAnalyticsFilters());
    act(() => {
      result.current.setFrom("2025-06-01");
    });
    const stored = JSON.parse(localStorage.getItem(STORAGE_KEY) ?? "{}");
    expect(stored.from).toBe("2025-06-01");
  });

  it("persists filters to localStorage when setProjectId is called", () => {
    const { result } = renderHook(() => useAnalyticsFilters());
    act(() => {
      result.current.setProjectId("my-project");
    });
    const stored = JSON.parse(localStorage.getItem(STORAGE_KEY) ?? "{}");
    expect(stored.projectId).toBe("my-project");
  });

  it("reads persisted filters from localStorage on mount", () => {
    // Pre-populate localStorage with saved filters
    localStorage.setItem(
      STORAGE_KEY,
      JSON.stringify({ from: "2025-01-01", to: "2025-01-31", projectId: "saved-proj" })
    );

    const { result } = renderHook(() => useAnalyticsFilters());

    expect(result.current.filters.from).toBe("2025-01-01");
    expect(result.current.filters.to).toBe("2025-01-31");
    expect(result.current.filters.projectId).toBe("saved-proj");
  });
});
