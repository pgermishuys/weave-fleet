/**
 * Tests for analytics-collector.ts — in-memory accumulation, batched flush, timer lifecycle.
 *
 * All dependencies are mocked. Tests use _resetForTests() to ensure clean state
 * between runs.
 */

import { vi, describe, it, expect, beforeEach, afterEach } from "vitest";

// Must mock process-manager before importing analytics-collector (which may
// transitively depend on it via db-repository or activity-emitter).
vi.mock("@/lib/server/process-manager", () => ({
  _recoveryComplete: Promise.resolve(),
}));

vi.mock("@/lib/server/db-repository", () => ({
  getSessionByHarnessId: vi.fn(() => undefined),
  incrementSessionTokens: vi.fn(() => undefined),
}));

vi.mock("@/lib/server/activity-emitter", () => ({
  emitTokenUpdate: vi.fn(),
}));

// db.transaction returns a function wrapping the passed fn — simulate by returning fn itself
const mockTransaction = vi.fn((fn: (...args: unknown[]) => unknown) => fn);
vi.mock("@/lib/server/database", () => ({
  getDb: vi.fn(() => ({
    transaction: mockTransaction,
  })),
}));

import {
  recordTokens,
  flushNow,
  getPendingCount,
  startCollector,
  stopCollector,
  _resetForTests,
} from "@/lib/server/analytics-collector";
import * as dbRepository from "@/lib/server/db-repository";
import * as activityEmitter from "@/lib/server/activity-emitter";

// ─── Setup ────────────────────────────────────────────────────────────────────

beforeEach(() => {
  _resetForTests();
  vi.clearAllMocks();
});

afterEach(() => {
  _resetForTests();
});

// ─── Tests ────────────────────────────────────────────────────────────────────

describe("analytics-collector", () => {
  describe("recordTokens", () => {
    it("RecordTokensDoesNotTriggerDbWrites", () => {
      recordTokens("s1", 100, 0.05);

      expect(dbRepository.incrementSessionTokens).not.toHaveBeenCalled();
      expect(activityEmitter.emitTokenUpdate).not.toHaveBeenCalled();
      expect(getPendingCount()).toBe(1);
    });

    it("MultipleRecordsForSameSessionAccumulate", () => {
      recordTokens("s1", 50, 0.01);
      recordTokens("s1", 30, 0.02);
      recordTokens("s1", 20, 0.01);

      expect(getPendingCount()).toBe(1);

      vi.mocked(dbRepository.getSessionByHarnessId).mockReturnValue({ id: "db-1" } as never);
      vi.mocked(dbRepository.incrementSessionTokens).mockReturnValue({ totalTokens: 100, totalCost: 0.04 });

      flushNow();

      expect(dbRepository.incrementSessionTokens).toHaveBeenCalledTimes(1);
      expect(dbRepository.incrementSessionTokens).toHaveBeenCalledWith("db-1", 100, 0.04);
    });

    it("MultipleSessionsAccumulateIndependently", () => {
      recordTokens("s1", 100, 0.05);
      recordTokens("s2", 200, 0.10);

      expect(getPendingCount()).toBe(2);

      vi.mocked(dbRepository.getSessionByHarnessId).mockImplementation((id: string) => {
        if (id === "s1") return { id: "db-1" } as never;
        if (id === "s2") return { id: "db-2" } as never;
        return undefined;
      });
      vi.mocked(dbRepository.incrementSessionTokens).mockReturnValue({ totalTokens: 100, totalCost: 0.05 });

      flushNow();

      expect(dbRepository.incrementSessionTokens).toHaveBeenCalledTimes(2);
      expect(dbRepository.incrementSessionTokens).toHaveBeenCalledWith("db-1", 100, 0.05);
      expect(dbRepository.incrementSessionTokens).toHaveBeenCalledWith("db-2", 200, 0.10);
    });
  });

  describe("flushNow", () => {
    it("FlushNowWritesAccumulatedTotalsToDb", () => {
      recordTokens("s1", 100, 0.05);

      vi.mocked(dbRepository.getSessionByHarnessId).mockReturnValue({ id: "db-1" } as never);
      vi.mocked(dbRepository.incrementSessionTokens).mockReturnValue({ totalTokens: 100, totalCost: 0.05 });

      flushNow();

      expect(dbRepository.incrementSessionTokens).toHaveBeenCalledWith("db-1", 100, 0.05);
      expect(activityEmitter.emitTokenUpdate).toHaveBeenCalledWith({
        sessionId: "s1",
        totalTokens: 100,
        totalCost: 0.05,
      });
      expect(getPendingCount()).toBe(0);
    });

    it("FlushEmitsTokenUpdateWithCorrectTotals", () => {
      recordTokens("s1", 100, 0.05);

      vi.mocked(dbRepository.getSessionByHarnessId).mockReturnValue({ id: "db-1" } as never);
      vi.mocked(dbRepository.incrementSessionTokens).mockReturnValue({ totalTokens: 500, totalCost: 1.25 });

      flushNow();

      expect(activityEmitter.emitTokenUpdate).toHaveBeenCalledTimes(1);
      expect(activityEmitter.emitTokenUpdate).toHaveBeenCalledWith({
        sessionId: "s1",
        totalTokens: 500,
        totalCost: 1.25,
      });
    });

    it("FlushClearsPendingMap", () => {
      recordTokens("s1", 100, 0.05);

      vi.mocked(dbRepository.getSessionByHarnessId).mockReturnValue({ id: "db-1" } as never);
      vi.mocked(dbRepository.incrementSessionTokens).mockReturnValue({ totalTokens: 100, totalCost: 0.05 });

      flushNow();
      expect(getPendingCount()).toBe(0);

      vi.clearAllMocks();

      // Second flush — nothing pending
      flushNow();
      expect(dbRepository.incrementSessionTokens).not.toHaveBeenCalled();
    });

    it("FlushSkipsUnknownSessions", () => {
      recordTokens("s-unknown", 100, 0.05);

      vi.mocked(dbRepository.getSessionByHarnessId).mockReturnValue(undefined);

      flushNow();

      expect(dbRepository.incrementSessionTokens).not.toHaveBeenCalled();
      expect(getPendingCount()).toBe(0);
    });

    it("FlushHandlesDbErrors", () => {
      recordTokens("s1", 100, 0.05);

      vi.mocked(dbRepository.getSessionByHarnessId).mockImplementation(() => {
        throw new Error("DB unavailable");
      });

      expect(() => flushNow()).not.toThrow();
      expect(getPendingCount()).toBe(0);
    });

    it("FlushDoesNotEmitWhenIncrementReturnsUndefined", () => {
      recordTokens("s1", 100, 0.05);

      vi.mocked(dbRepository.getSessionByHarnessId).mockReturnValue({ id: "db-1" } as never);
      vi.mocked(dbRepository.incrementSessionTokens).mockReturnValue(undefined);

      flushNow();

      expect(dbRepository.incrementSessionTokens).toHaveBeenCalledWith("db-1", 100, 0.05);
      expect(activityEmitter.emitTokenUpdate).not.toHaveBeenCalled();
    });

    it("FlushWrapsDbWritesInTransaction", () => {
      recordTokens("s1", 100, 0.05);
      recordTokens("s2", 200, 0.10);

      vi.mocked(dbRepository.getSessionByHarnessId).mockImplementation((id: string) => {
        if (id === "s1") return { id: "db-1" } as never;
        if (id === "s2") return { id: "db-2" } as never;
        return undefined;
      });
      vi.mocked(dbRepository.incrementSessionTokens).mockReturnValue({ totalTokens: 100, totalCost: 0.05 });

      flushNow();

      expect(mockTransaction).toHaveBeenCalled();
    });
  });

  describe("startCollector / stopCollector", () => {
    it("StartCollectorIsIdempotent", () => {
      expect(() => {
        startCollector(5000);
        startCollector(5000);
      }).not.toThrow();
    });

    it("StopCollectorIsIdempotent", () => {
      startCollector(5000);
      expect(() => {
        stopCollector();
        stopCollector();
      }).not.toThrow();
    });

    it("StopCollectorWithoutStartIsNoOp", () => {
      expect(() => stopCollector()).not.toThrow();
    });

    it("TimerFlushesAutomatically", async () => {
      vi.mocked(dbRepository.getSessionByHarnessId).mockReturnValue({ id: "db-1" } as never);
      vi.mocked(dbRepository.incrementSessionTokens).mockReturnValue({ totalTokens: 100, totalCost: 0.05 });

      startCollector(50); // 50ms interval for fast test
      recordTokens("s1", 100, 0.05);

      await new Promise((r) => setTimeout(r, 150));

      expect(dbRepository.incrementSessionTokens).toHaveBeenCalled();
    });
  });

  describe("_resetForTests", () => {
    it("ResetForTestsClearsAllState", async () => {
      recordTokens("s1", 100, 0.05);
      startCollector(50);

      _resetForTests();

      expect(getPendingCount()).toBe(0);

      // Record more tokens and wait — no flush should occur
      vi.clearAllMocks();
      recordTokens("s1", 50, 0.01);
      await new Promise((r) => setTimeout(r, 150));

      expect(dbRepository.incrementSessionTokens).not.toHaveBeenCalled();
    });
  });
});
