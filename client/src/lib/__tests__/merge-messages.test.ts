import { describe, it, expect } from "vitest";
import { mergeMessagesByTimestamp } from "../merge-messages";

interface TestMessage {
  id: string;
  createdAt?: number;
  role: string;
}

describe("mergeMessagesByTimestamp", () => {
  it("should concatenate delivered then optimistic", () => {
    const delivered: TestMessage[] = [
      { id: "d1", createdAt: 100, role: "user" },
      { id: "d2", createdAt: 300, role: "assistant" },
    ];

    const optimistic: TestMessage[] = [
      { id: "o1", createdAt: 200, role: "user" },
    ];

    const result = mergeMessagesByTimestamp(delivered, optimistic);

    // Delivered first, then optimistic (no interleaving)
    expect(result.map((m) => m.id)).toEqual(["d1", "d2", "o1"]);
  });

  it("should preserve delivered order", () => {
    const delivered: TestMessage[] = [
      { id: "d1", createdAt: 100, role: "user" },
      { id: "d2", createdAt: 100, role: "assistant" },
    ];

    const optimistic: TestMessage[] = [
      { id: "o1", createdAt: 100, role: "user" },
    ];

    const result = mergeMessagesByTimestamp(delivered, optimistic);

    // Delivered order preserved, then optimistic
    expect(result.map((m) => m.id)).toEqual(["d1", "d2", "o1"]);
  });

  it("should preserve optimistic order", () => {
    const delivered: TestMessage[] = [];

    const optimistic: TestMessage[] = [
      { id: "o1", createdAt: 100, role: "user" },
      { id: "o2", createdAt: 100, role: "user" },
      { id: "o3", createdAt: 100, role: "user" },
    ];

    const result = mergeMessagesByTimestamp(delivered, optimistic);

    expect(result.map((m) => m.id)).toEqual(["o1", "o2", "o3"]);
  });

  it("should place optimistic after delivered regardless of timestamp", () => {
    const delivered: TestMessage[] = [
      { id: "d1", createdAt: 300, role: "user" },
      { id: "d2", role: "assistant" },
    ];

    const optimistic: TestMessage[] = [
      { id: "o1", createdAt: 100, role: "user" },
    ];

    const result = mergeMessagesByTimestamp(delivered, optimistic);

    // Delivered first (even with later timestamp), then optimistic
    expect(result.map((m) => m.id)).toEqual(["d1", "d2", "o1"]);
  });

  it("should handle empty delivered array", () => {
    const delivered: TestMessage[] = [];

    const optimistic: TestMessage[] = [
      { id: "o1", createdAt: 100, role: "user" },
      { id: "o2", createdAt: 200, role: "user" },
    ];

    const result = mergeMessagesByTimestamp(delivered, optimistic);

    expect(result.map((m) => m.id)).toEqual(["o1", "o2"]);
  });

  it("should handle empty optimistic array", () => {
    const delivered: TestMessage[] = [
      { id: "d1", createdAt: 100, role: "user" },
      { id: "d2", createdAt: 200, role: "assistant" },
    ];

    const optimistic: TestMessage[] = [];

    const result = mergeMessagesByTimestamp(delivered, optimistic);

    expect(result.map((m) => m.id)).toEqual(["d1", "d2"]);
  });

  it("should handle both arrays empty", () => {
    const delivered: TestMessage[] = [];
    const optimistic: TestMessage[] = [];

    const result = mergeMessagesByTimestamp(delivered, optimistic);

    expect(result).toEqual([]);
  });
});
