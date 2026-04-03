import { describe, it, expect } from "vitest";
import type { SessionActivityStatus } from "@/lib/types";
import { patchActivityStatus, activityToSessionStatus, pruneStalePatches } from "@/contexts/sessions-context";
import type { SessionListItem } from "@/lib/api-types";

// ─── Helpers ──────────────────────────────────────────────────────────────────

function makeSession(id: string, overrides: Partial<SessionListItem> = {}): SessionListItem {
  return {
    instanceId: "inst-1",
    workspaceId: "ws-1",
    workspaceDirectory: "/tmp/proj",
    workspaceDisplayName: null,
    isolationStrategy: "existing",
    sourceDirectory: null,
    branch: null,
    sessionStatus: "idle",
    instanceStatus: "running",
    session: { id } as SessionListItem["session"],
    activityStatus: "idle",
    lifecycleStatus: "running",
    typedInstanceStatus: "running",
    ...overrides,
  };
}

// ─── activityToSessionStatus ──────────────────────────────────────────────────

describe("activityToSessionStatus", () => {
  it("MapsBusyToActive", () => {
    expect(activityToSessionStatus("busy")).toBe("active");
  });

  it("MapsIdleToIdle", () => {
    expect(activityToSessionStatus("idle")).toBe("idle");
  });

  it("MapsWaitingInputToWaitingInput", () => {
    expect(activityToSessionStatus("waiting_input")).toBe("waiting_input");
  });
});

// ─── patchActivityStatus ──────────────────────────────────────────────────────

describe("patchActivityStatus", () => {
  it("PatchesMatchingSession", () => {
    const sessions = [makeSession("sess-1", { activityStatus: "idle", sessionStatus: "idle" })];
    const result = patchActivityStatus(sessions, "sess-1", "busy");

    expect(result).not.toBe(sessions);
    expect(result[0]!.activityStatus).toBe("busy");
    expect(result[0]!.sessionStatus).toBe("active");
  });

  it("ReturnsSameArrayWhenSessionNotFound", () => {
    const sessions = [makeSession("sess-1"), makeSession("sess-2")];
    const result = patchActivityStatus(sessions, "sess-nonexistent", "busy");

    expect(result).toBe(sessions);
  });

  it("ReturnsSameArrayWhenStatusUnchanged", () => {
    const sessions = [makeSession("sess-1", { activityStatus: "busy" })];
    const result = patchActivityStatus(sessions, "sess-1", "busy");

    expect(result).toBe(sessions);
  });

  it("DoesNotMutateOriginalArray", () => {
    const original = makeSession("sess-1", { activityStatus: "idle" });
    const sessions = [original];
    const result = patchActivityStatus(sessions, "sess-1", "busy");

    expect(result).not.toBe(sessions);
    // Original session object is unchanged
    expect(sessions[0]!.activityStatus).toBe("idle");
    expect(original.activityStatus).toBe("idle");
  });

  it("PatchesCorrectSessionAmongMultiple", () => {
    const sessions = [
      makeSession("sess-1", { activityStatus: "idle", sessionStatus: "idle" }),
      makeSession("sess-2", { activityStatus: "idle", sessionStatus: "idle" }),
      makeSession("sess-3", { activityStatus: "idle", sessionStatus: "idle" }),
    ];
    const result = patchActivityStatus(sessions, "sess-2", "waiting_input");

    expect(result).not.toBe(sessions);
    expect(result[0]!.activityStatus).toBe("idle");
    expect(result[0]!.sessionStatus).toBe("idle");
    expect(result[1]!.activityStatus).toBe("waiting_input");
    expect(result[1]!.sessionStatus).toBe("waiting_input");
    expect(result[2]!.activityStatus).toBe("idle");
    expect(result[2]!.sessionStatus).toBe("idle");
    // Unchanged sessions retain same object references
    expect(result[0]).toBe(sessions[0]);
    expect(result[2]).toBe(sessions[2]);
  });
});

// ─── pruneStalePatches ────────────────────────────────────────────────────────

describe("pruneStalePatches", () => {
  it("ReturnsSameMapWhenPatchesAreEmpty", () => {
    const patches = new Map<string, SessionActivityStatus>();
    const sessions = [makeSession("sess-1")];
    const result = pruneStalePatches(patches, sessions);

    expect(result).toBe(patches);
  });

  it("KeepsPatchWhenPolledStatusDiffersFromPatch", () => {
    const patches = new Map<string, SessionActivityStatus>([
      ["sess-1", "busy"],
    ]);
    const sessions = [makeSession("sess-1", { activityStatus: "idle" })];
    const result = pruneStalePatches(patches, sessions);

    expect(result.size).toBe(1);
    expect(result.get("sess-1")).toBe("busy");
  });

  it("PrunesPatchWhenPolledStatusMatchesPatch", () => {
    const patches = new Map<string, SessionActivityStatus>([
      ["sess-1", "busy"],
    ]);
    const sessions = [makeSession("sess-1", { activityStatus: "busy" })];
    const result = pruneStalePatches(patches, sessions);

    expect(result.size).toBe(0);
  });

  it("PrunesPatchWhenSessionNotInPollResults", () => {
    const patches = new Map<string, SessionActivityStatus>([
      ["sess-deleted", "busy"],
    ]);
    const sessions = [makeSession("sess-1")];
    const result = pruneStalePatches(patches, sessions);

    expect(result.size).toBe(0);
  });

  it("HandlesMultiplePatchesWithMixedOutcomes", () => {
    const patches = new Map<string, SessionActivityStatus>([
      ["sess-1", "busy"],           // poll says idle → keep
      ["sess-2", "idle"],           // poll says idle → prune (matches)
      ["sess-3", "waiting_input"],  // not in poll → prune
    ]);
    const sessions = [
      makeSession("sess-1", { activityStatus: "idle" }),
      makeSession("sess-2", { activityStatus: "idle" }),
    ];
    const result = pruneStalePatches(patches, sessions);

    expect(result.size).toBe(1);
    expect(result.get("sess-1")).toBe("busy");
    expect(result.has("sess-2")).toBe(false);
    expect(result.has("sess-3")).toBe(false);
  });

  it("PrunesAllPatchesWhenAllMatchPolledStatus", () => {
    const patches = new Map<string, SessionActivityStatus>([
      ["sess-1", "busy"],
      ["sess-2", "idle"],
    ]);
    const sessions = [
      makeSession("sess-1", { activityStatus: "busy" }),
      makeSession("sess-2", { activityStatus: "idle" }),
    ];
    const result = pruneStalePatches(patches, sessions);

    expect(result.size).toBe(0);
  });
});
