/**
 * Compile-time and runtime type contract tests for analytics API types.
 * These tests verify that our TypeScript types accurately model the API response shapes.
 * We use TypeScript `satisfies` for compile-time type checking and `expect()` for runtime.
 */

import type {
  AnalyticsSummary,
  AnalyticsTopItem,
  DailyAnalytics,
  SessionAnalytics,
  ModelAnalytics,
} from "@/lib/api-types";

// ─── AnalyticsSummary ────────────────────────────────────────────────────────

describe("AnalyticsSummary type", () => {
  it("accepts a well-formed summary fixture", () => {
    const fixture = {
      totalTokens: 125_000,
      totalCost: 45.67,
      totalEstimatedCost: 50.00,
      sessionCount: 42,
      messageCount: 1_284,
      topModels: [
        { name: "claude-sonnet-4", tokens: 80_000, cost: 32.50 },
        { name: "gpt-4o", tokens: 45_000, cost: 13.17 },
      ],
      topProjects: [
        { name: "my-project", tokens: 100_000, cost: 40.00 },
      ],
    } satisfies AnalyticsSummary;

    expect(fixture.totalTokens).toBe(125_000);
    expect(fixture.sessionCount).toBe(42);
    expect(fixture.topModels).toHaveLength(2);
    expect(fixture.topProjects).toHaveLength(1);
  });

  it("requires totalTokens to be a number", () => {
    const item: AnalyticsSummary = {
      totalTokens: 0,
      totalCost: 0,
      totalEstimatedCost: 0,
      sessionCount: 0,
      messageCount: 0,
      topModels: [],
      topProjects: [],
    };
    expect(typeof item.totalTokens).toBe("number");
    expect(typeof item.totalCost).toBe("number");
  });
});

// ─── AnalyticsTopItem ─────────────────────────────────────────────────────────

describe("AnalyticsTopItem type", () => {
  it("accepts a well-formed top item", () => {
    const item: AnalyticsTopItem = {
      name: "my-project",
      tokens: 50_000,
      cost: 18.75,
    };
    expect(item.name).toBe("my-project");
    expect(item.tokens).toBe(50_000);
  });
});

// ─── DailyAnalytics ──────────────────────────────────────────────────────────

describe("DailyAnalytics type", () => {
  it("accepts a well-formed daily entry", () => {
    const fixture: DailyAnalytics = {
      date: "2025-01-15",
      tokens: 12_500,
      cost: 4.80,
      estimatedCost: 5.00,
      sessions: 3,
      messages: 42,
    };
    expect(fixture.date).toBe("2025-01-15");
    expect(fixture.sessions).toBe(3);
  });

  it("parses an array of daily entries", () => {
    const fixtures: DailyAnalytics[] = [
      { date: "2025-01-01", tokens: 1_000, cost: 0.5, estimatedCost: 0.6, sessions: 1, messages: 5 },
      { date: "2025-01-02", tokens: 2_000, cost: 1.0, estimatedCost: 1.2, sessions: 2, messages: 10 },
    ];
    expect(fixtures).toHaveLength(2);
    expect(fixtures[0].date).toBe("2025-01-01");
    expect(fixtures[1].tokens).toBe(2_000);
  });
});

// ─── SessionAnalytics ─────────────────────────────────────────────────────────

describe("SessionAnalytics type", () => {
  it("accepts a full session entry", () => {
    const fixture: SessionAnalytics = {
      sessionId: "ses_abc123",
      title: "My Test Session",
      projectId: "proj_xyz",
      projectName: "My Project",
      tokens: 5_000,
      cost: 1.75,
      estimatedCost: 2.00,
      models: ["anthropic/claude-sonnet-4"],
      durationSeconds: 120,
      createdAt: "2025-01-15T10:30:00Z",
    };
    expect(fixture.sessionId).toBe("ses_abc123");
    expect(fixture.models).toHaveLength(1);
  });

  it("accepts nullable fields as null", () => {
    const fixture: SessionAnalytics = {
      sessionId: "ses_def456",
      title: null,
      projectId: null,
      projectName: null,
      tokens: 0,
      cost: 0,
      estimatedCost: 0,
      models: [],
      durationSeconds: null,
      createdAt: "2025-01-16T00:00:00Z",
    };
    expect(fixture.title).toBeNull();
    expect(fixture.projectId).toBeNull();
    expect(fixture.durationSeconds).toBeNull();
  });
});

// ─── ModelAnalytics ───────────────────────────────────────────────────────────

describe("ModelAnalytics type", () => {
  it("accepts a well-formed model entry", () => {
    const fixture: ModelAnalytics = {
      modelId: "anthropic/claude-sonnet-4",
      providerId: "anthropic",
      tokens: 80_000,
      cost: 32.50,
      estimatedCost: 35.00,
      messageCount: 342,
      avgCostPerMessage: 0.095,
    };
    expect(fixture.modelId).toBe("anthropic/claude-sonnet-4");
    expect(fixture.avgCostPerMessage).toBeCloseTo(0.095);
  });

  it("parses an array of model entries", () => {
    const fixtures: ModelAnalytics[] = [
      {
        modelId: "openai/gpt-4o",
        providerId: "openai",
        tokens: 45_000,
        cost: 13.17,
        estimatedCost: 14.00,
        messageCount: 120,
        avgCostPerMessage: 0.11,
      },
    ];
    expect(fixtures).toHaveLength(1);
    expect(fixtures[0].providerId).toBe("openai");
  });
});
