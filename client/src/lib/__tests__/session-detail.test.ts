import { describe, expect, it } from "vitest";
import {
  buildSessionListItem,
  isActiveActivityStatus,
  isDiffStalingStatus,
  normalizeActivityStatus,
  normalizeLifecycleStatus,
  normalizeRetentionStatus,
  normalizeSessionDetailResponse,
} from "@/lib/session-detail";

describe("normalizeSessionDetailResponse", () => {
  it("returns an empty detail for anything that is not an object", () => {
    expect(normalizeSessionDetailResponse(null)).toEqual({});
    expect(normalizeSessionDetailResponse("text")).toEqual({});
  });

  it("reads camelCase and PascalCase keys, camelCase first", () => {
    expect(normalizeSessionDetailResponse({ Id: "a", Title: "Pascal" }).title).toBe("Pascal");
    expect(normalizeSessionDetailResponse({ title: "camel", Title: "Pascal" }).title).toBe("camel");
  });

  it("maps a missing or null field to null and a wrong type to undefined", () => {
    const detail = normalizeSessionDetailResponse({ title: null, branch: 5, totalTokens: "x" });
    expect(detail.title).toBeNull();
    expect(detail.branch).toBeUndefined();
    expect(detail.projectId).toBeNull();
    expect(detail.totalTokens).toBeUndefined();
  });

  it("fills an origin's missing strings and keeps a null origin null", () => {
    expect(normalizeSessionDetailResponse({ origin: {} }).origin).toEqual({
      sourceType: "", title: null, resourceUrl: null, resourceId: null, providerId: "",
    });
    expect(normalizeSessionDetailResponse({ origin: null }).origin).toBeNull();
    expect(normalizeSessionDetailResponse({}).origin).toBeNull();
  });

  it("keeps tags only when they are an array", () => {
    expect(normalizeSessionDetailResponse({ tags: ["a"] }).tags).toEqual(["a"]);
    expect(normalizeSessionDetailResponse({ tags: "a" }).tags).toBeUndefined();
  });
});

describe("status helpers", () => {
  it("maps lifecycle names onto the five lifecycle states", () => {
    expect(normalizeLifecycleStatus("delegating")).toBe("running");
    expect(normalizeLifecycleStatus("complete")).toBe("completed");
    expect(normalizeLifecycleStatus("nope")).toBeNull();
    expect(normalizeLifecycleStatus(undefined)).toBeNull();
  });

  it("maps activity names, with active as busy", () => {
    expect(normalizeActivityStatus("active")).toBe("busy");
    expect(normalizeActivityStatus("retry")).toBe("retry");
    expect(normalizeActivityStatus("running")).toBeNull();
  });

  it("treats anything but archived as active retention", () => {
    expect(normalizeRetentionStatus("archived")).toBe("archived");
    expect(normalizeRetentionStatus(null)).toBe("active");
  });

  it("counts busy, delegating and retry as active, and a waiting running session as staling diffs", () => {
    expect(isActiveActivityStatus("retry")).toBe(true);
    expect(isActiveActivityStatus("idle")).toBe(false);
    expect(isDiffStalingStatus("waiting_input", "running")).toBe(true);
    expect(isDiffStalingStatus("waiting_input", "completed")).toBe(false);
    expect(isDiffStalingStatus("busy", "completed")).toBe(true);
  });
});

describe("buildSessionListItem", () => {
  it("prefers the detail, then the search instance, then defaults", () => {
    const item = buildSessionListItem("s1", {}, null, "inst-search");
    expect(item).toMatchObject({
      instanceId: "inst-search", session: { id: "s1", title: "Untitled session", tags: [] },
      lifecycleStatus: "running", activityStatus: "idle", sessionStatus: "idle", harnessType: "",
    });
    expect(buildSessionListItem("s1", { instanceId: "inst-api" }, null, "inst-search").instanceId).toBe("inst-api");
  });

  it("adds retry fields only for a retrying session", () => {
    expect(buildSessionListItem("s1", { activityStatus: "retry", retryAttempt: 3 }, null, undefined)).toMatchObject({ retryAttempt: 3 });
    expect(buildSessionListItem("s1", { activityStatus: "busy", retryAttempt: 3 }, null, undefined)).not.toHaveProperty("retryAttempt");
  });
});
