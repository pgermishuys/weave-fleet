import { beforeEach, describe, expect, it } from "vitest";
import { createPinia, setActivePinia } from "pinia";
import type { SessionListItem } from "@/lib/api-types";
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
});
