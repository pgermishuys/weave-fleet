import { beforeEach, describe, expect, it } from "vitest";
import { createPinia, setActivePinia } from "pinia";
import type { SessionListItem } from "@/api/client";
import { useSessionsStore } from "@/stores/sessions";

function createSessionListItem(): SessionListItem {
  return {
    harnessType: "opencode",
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

  it("moves a workflow run's steps to a project together, and nothing else", () => {
    const store = useSessionsStore();
    const step = (id: string, workflowRunId: string | null): SessionListItem => {
      const item = createSessionListItem();
      return { ...item, session: { ...item.session, id }, projectId: "scratch", projectName: "Scratch", workflowRunId };
    };
    store.setSessions([step("plan", "run-1"), step("implement", "run-1"), step("other", "run-2"), step("plain", null)]);

    store.patchSessionProject("implement", "project-2", "Site");

    expect(store.sessions.map((item) => [item.session.id, item.projectId, item.projectName])).toEqual([
      ["plan", "project-2", "Site"],
      ["implement", "project-2", "Site"],
      ["other", "scratch", "Scratch"],
      ["plain", "scratch", "Scratch"],
    ]);
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

describe("sessions on other machines opened here", () => {
  beforeEach(() => {
    setActivePinia(createPinia());
  });

  function onMini(id: string, title = "Hero image sizes"): SessionListItem {
    const item = createSessionListItem();
    return { ...item, session: { ...item.session, id, title } };
  }

  it("finds them by id without adding them to the live machine's list", () => {
    const store = useSessionsStore();
    store.setSessions([createSessionListItem()]);

    store.upsertElsewhere("mini-id", onMini("mini-1"));

    expect(store.sessions.map((item) => item.session.id)).toEqual(["session-1"]);
    expect(store.sessionById("mini-1")?.session.title).toBe("Hero image sizes");
    expect(store.sessionById("session-1")?.session.title).toBe("Migration");
    expect(store.sessionById("nowhere")).toBeNull();
    expect(store.elsewhere.get("mini-1")?.machineKey).toBe("mini-id");
  });

  it("patches and removes them like the live machine's", () => {
    const store = useSessionsStore();
    store.upsertElsewhere("mini-id", onMini("mini-1"));
    store.setActiveSessionId("mini-1");

    store.patchSession("mini-1", { activityStatus: "idle", sessionStatus: "idle" });
    expect(store.sessionById("mini-1")).toMatchObject({ activityStatus: "idle", sessionStatus: "idle" });

    store.removeSession("mini-1");
    expect(store.sessionById("mini-1")).toBeNull();
    expect(store.activeSessionId).toBeNull();
  });

  it("keeps the 20 opened most recently", () => {
    const store = useSessionsStore();
    for (let index = 0; index < 22; index += 1) store.upsertElsewhere("mini-id", onMini(`mini-${index}`));
    // Opening one again makes it the newest.
    store.upsertElsewhere("mini-id", onMini("mini-2", "Opened again"));

    expect(store.elsewhere.size).toBe(20);
    expect(store.sessionById("mini-0")).toBeNull();
    expect(store.sessionById("mini-1")).toBeNull();
    expect(store.sessionById("mini-2")?.session.title).toBe("Opened again");
    expect(store.sessionById("mini-3")).not.toBeNull();
  });

  it("forgets a machine's sessions when the machine is forgotten", () => {
    const store = useSessionsStore();
    store.upsertElsewhere("mini-id", onMini("mini-1"));
    store.upsertElsewhere("lab-id", onMini("lab-1", "Index for slow search"));

    store.forgetElsewhere("mini-id");

    expect([...store.elsewhere.keys()]).toEqual(["lab-1"]);
  });
});
