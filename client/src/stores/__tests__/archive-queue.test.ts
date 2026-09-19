import { beforeEach, describe, expect, it, vi } from "vitest";
import { shallowRef } from "vue";
import type { SessionListItem } from "@/api/client";

const archiveSession = vi.fn<(sessionId: string) => Promise<void>>();
const unarchiveSession = vi.fn<(sessionId: string) => Promise<void>>();

vi.mock("@/composables/use-session-actions", () => ({
  useArchiveSession: () => ({ archiveSession, isArchiving: shallowRef(false), error: shallowRef(undefined) }),
  useUnarchiveSession: () => ({ unarchiveSession, isUnarchiving: shallowRef(false), error: shallowRef(undefined) }),
}));

const { ARCHIVE_UNDO_MS, useArchiveQueueStore } = await import("@/stores/archive-queue");
const { useSessionsStore } = await import("@/stores/sessions");

function session(id: string, title: string, retentionStatus: "active" | "archived" = "active"): SessionListItem {
  return {
    instanceId: `instance-${id}`,
    session: { id, title, time: { created: 1, updated: 2 }, tags: [] },
    retentionStatus,
  } as unknown as SessionListItem;
}

describe("archive queue", () => {
  beforeEach(() => {
    vi.useFakeTimers();
    archiveSession.mockReset().mockResolvedValue();
    unarchiveSession.mockReset().mockResolvedValue();
    useSessionsStore().setSessions([session("a", "Alpha"), session("b", "Beta"), session("c", "Gamma", "archived")]);
  });

  it("holds an archive back until the undo window closes", async () => {
    const queue = useArchiveQueueStore();

    queue.archive(["a", "b"]);

    expect(queue.pending?.message).toBe("Archived 2 sessions");
    expect(queue.pendingIds.has("a")).toBe(true);
    expect(archiveSession).not.toHaveBeenCalled();

    await vi.advanceTimersByTimeAsync(ARCHIVE_UNDO_MS);

    expect(archiveSession.mock.calls.map(([id]) => id)).toEqual(["a", "b"]);
    expect(queue.pending).toBeNull();
    const sessions = useSessionsStore();
    expect(sessions.sessions.find((item) => item.session.id === "a")?.retentionStatus).toBe("archived");
    expect(sessions.sessionStateOverrides.a?.retentionStatus).toBe("archived");
  });

  it("never reaches the server when undone", async () => {
    const queue = useArchiveQueueStore();

    queue.archive(["a"]);
    expect(queue.pending?.message).toBe('Archived "Alpha"');
    queue.undo();
    await vi.advanceTimersByTimeAsync(ARCHIVE_UNDO_MS * 2);

    expect(archiveSession).not.toHaveBeenCalled();
    expect(queue.pendingIds.size).toBe(0);
  });

  it("sends the waiting archive when another one starts", async () => {
    const queue = useArchiveQueueStore();

    queue.archive(["a"]);
    queue.archive(["b"]);
    await vi.advanceTimersByTimeAsync(0);

    expect(archiveSession.mock.calls.map(([id]) => id)).toEqual(["a"]);
    expect(queue.pending?.ids).toEqual(["b"]);
  });

  it("names the sessions it couldn't archive", async () => {
    archiveSession.mockRejectedValueOnce(new Error("boom"));
    const queue = useArchiveQueueStore();

    queue.archive(["a"]);
    await vi.advanceTimersByTimeAsync(ARCHIVE_UNDO_MS);

    expect(queue.error).toBe('Couldn\'t archive "Alpha".');
    expect(useSessionsStore().sessions.find((item) => item.session.id === "a")?.retentionStatus).toBe("active");
  });

  it("restores an archived session", async () => {
    const queue = useArchiveQueueStore();

    await queue.restore("c");

    expect(unarchiveSession).toHaveBeenCalledWith("c");
    const sessions = useSessionsStore();
    expect(sessions.sessions.find((item) => item.session.id === "c")?.retentionStatus).toBe("active");
    expect(sessions.sessionStateOverrides.c?.retentionStatus).toBe("active");
  });
});
