import { beforeEach, describe, expect, it } from "vitest";
import { createPinia, setActivePinia } from "pinia";
import { useBoardStore } from "@/stores/board";

describe("useBoardStore", () => {
  beforeEach(() => {
    setActivePinia(createPinia());
  });

  it("derives the default quick stats from seeded sessions", () => {
    const store = useBoardStore();

    expect(store.quickStats).toEqual({
      total: 8,
      visible: 8,
      active: 4,
      completed: 1,
      projects: 8,
    });
    expect(store.filterSummary).toBe("8 visible • grouped by status");
    expect(store.groupedSessions.map((group) => group.key)).toEqual([
      "active",
      "idle",
      "waiting_input",
      "completed",
      "error",
    ]);
  });

  it("filters by project, status, and agent", () => {
    const store = useBoardStore();

    store.setSelectedProject("Api");
    expect(store.filteredSessions).toHaveLength(1);
    expect(store.filteredSessions[0]?.projectName).toBe("Api");

    store.setSelectedProject("all");
    store.setStatusFilter("active", false);
    expect(store.filteredSessions.every((session) => session.status !== "active")).toBe(true);

    store.setAgentFilter("loom", false);
    expect(store.filteredSessions.every((session) => session.agent !== "loom")).toBe(true);
  });

  it("groups and sorts sessions by the selected strategy", () => {
    const store = useBoardStore();

    store.setGroupBy("agent");
    store.setSortBy("title");

    expect(store.groupedSessions.map((group) => group.label)).toEqual(["loom", "shuttle", "tapestry"]);
    expect(store.groupedSessions[0]?.sessions.map((session) => session.title)).toEqual([
      "Analyze the payment processing flow and identify bottlenecks",
      "Build the settings page with dark mode toggle",
      "Generate API documentation from the codebase",
      "Refactor the database connection pool to use singleton pattern",
    ]);
  });
});
