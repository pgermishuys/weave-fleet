import { beforeEach, describe, expect, it } from "vitest";
import { createPinia, setActivePinia } from "pinia";
import type { SessionListItem } from "@/api/client";
import { useSessionsStore } from "@/stores/sessions";

function createSessionListItem(): SessionListItem {
  return {
    instanceId: "instance-1",
    workspaceId: "workspace-1",
    workspaceDirectory: "/tmp/project",
    workspaceDisplayName: "project",
    isolationStrategy: "existing",
    sessionStatus: "active",
    session: {
      id: "session-1",
      title: "Migration",
      time: {
        created: 1,
        updated: 2,
      },
      tags: [],
    },
    instanceStatus: "running",
    parentSessionId: null,
    sourceDirectory: "/tmp/project",
    branch: "main",
    activityStatus: "busy",
    lifecycleStatus: "running",
    retentionStatus: "active",
    archivedAt: null,
    typedInstanceStatus: "running",
    isHidden: false,
    projectId: "project-1",
    projectName: "Api",
    totalTokens: 123,
    totalCost: 4.56,
    tags: [],
  };
}

describe("useSessionsStore", () => {
  beforeEach(() => {
    setActivePinia(createPinia());
  });

  it("stores session list items and the active selection", () => {
    const store = useSessionsStore();

    expect(store.sessions).toEqual([]);
    expect(store.activeSessionId).toBeNull();

    store.sessions = [createSessionListItem()];
    store.activeSessionId = "session-1";

    expect(store.sessions).toHaveLength(1);
    expect(store.sessions[0]?.session.title).toBe("Migration");
    expect(store.activeSessionId).toBe("session-1");
  });

  it("puts a session it hasn't seen at the top, where the newest-first list will have it", () => {
    const store = useSessionsStore();
    const existing = createSessionListItem();
    store.setSessions([existing]);

    const created = createSessionListItem();
    created.session = { ...created.session, id: "session-2", title: "Just created" };
    store.upsertSession(created);

    expect(store.sessions.map((item) => item.session.id)).toEqual(["session-2", "session-1"]);
  });

  it("updates a known session in place", () => {
    const store = useSessionsStore();
    const first = createSessionListItem();
    const second = createSessionListItem();
    second.session = { ...second.session, id: "session-2" };
    store.setSessions([first, second]);

    store.upsertSession({ ...second, session: { ...second.session, title: "Renamed" } });

    expect(store.sessions.map((item) => item.session.id)).toEqual(["session-1", "session-2"]);
    expect(store.sessions[1]?.session.title).toBe("Renamed");
  });
});
