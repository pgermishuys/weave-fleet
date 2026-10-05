import { beforeEach, describe, expect, it, vi } from "vitest";
import type { SessionListItem } from "@/api/client";

const updateSessionLineage = vi.fn<(sessionId: string, detached: boolean) => Promise<void>>();

vi.mock("@/composables/use-session-actions", () => ({
  updateSessionLineage,
  useArchiveSession: () => ({ archiveSession: vi.fn() }),
  useUnarchiveSession: () => ({ unarchiveSession: vi.fn() }),
}));

const { LINEAGE_UNDO_MS, useLineageMovesStore } = await import("@/stores/lineage-moves");
const { useSessionsStore } = await import("@/stores/sessions");

function session(id: string, title: string, extra: Partial<SessionListItem> = {}): SessionListItem {
  return { instanceId: `instance-${id}`, session: { id, title, time: { created: 1, updated: 2 }, tags: [] }, ...extra } as unknown as SessionListItem;
}

function detachedAt(id: string): string | null | undefined {
  return useSessionsStore().sessions.find((item) => item.session.id === id)?.lineageDetachedAt;
}

describe("lineage moves", () => {
  beforeEach(() => {
    vi.useFakeTimers();
    updateSessionLineage.mockReset().mockResolvedValue();
    useSessionsStore().setSessions([
      session("capture", "Capture subagents"),
      session("fork", "Fork: emit jobs", { forkedFromSessionId: "capture", spawnKind: "fork" }),
    ]);
  });

  it("moves a session out at once, tells the server, and offers Undo for a while", async () => {
    const moves = useLineageMovesStore();

    await moves.moveOut("fork");

    expect(updateSessionLineage).toHaveBeenCalledWith("fork", true);
    expect(detachedAt("fork")).toBeTruthy();
    expect(moves.pending?.message).toBe('Moved "Fork: emit jobs" out of "Capture subagents"');

    await vi.advanceTimersByTimeAsync(LINEAGE_UNDO_MS);
    expect(moves.pending).toBeNull();
  });

  it("puts it back under its parent on Undo", async () => {
    const moves = useLineageMovesStore();
    await moves.moveOut("fork");

    await moves.undo();

    expect(updateSessionLineage.mock.calls).toEqual([["fork", true], ["fork", false]]);
    expect(detachedAt("fork")).toBeNull();
    expect(moves.pending).toBeNull();
  });

  it("moves a session back under its parent, and Undo moves it out again", async () => {
    useSessionsStore().patchSession("fork", { lineageDetachedAt: "2026-10-04T12:00:00Z" });
    const moves = useLineageMovesStore();

    await moves.moveBack("fork");
    expect(detachedAt("fork")).toBeNull();
    expect(moves.pending?.message).toBe('Moved "Fork: emit jobs" back under "Capture subagents"');

    await moves.undo();
    expect(updateSessionLineage.mock.calls).toEqual([["fork", false], ["fork", true]]);
    expect(detachedAt("fork")).toBeTruthy();
  });

  it("puts the list back and says why when the server refuses", async () => {
    updateSessionLineage.mockRejectedValueOnce(new Error("A subagent's session belongs to its parent's turn; it can't be moved out."));
    const moves = useLineageMovesStore();

    await moves.moveOut("fork");

    expect(detachedAt("fork")).toBeFalsy();
    expect(moves.pending).toBeNull();
    expect(moves.error).toBe("A subagent's session belongs to its parent's turn; it can't be moved out.");
  });
});
