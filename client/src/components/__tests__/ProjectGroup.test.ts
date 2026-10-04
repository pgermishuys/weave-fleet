import { flushPromises, mount } from "@vue/test-utils";
import { describe, expect, it, vi } from "vitest";
import ProjectGroup from "@/components/sessions/ProjectGroup.vue";
import { toRunningWorkItem, type RunningWorkItem } from "@/lib/running-work";

const { navigate } = vi.hoisted(() => ({ navigate: vi.fn() }));
vi.mock("@tanstack/vue-router", () => ({ useRouter: () => ({ navigate }) }));

function createProjectGroup() {
  return {
    id: "project-2",
    projectId: "project-2",
    name: "Project B",
    color: "#22c55e",
    isUngrouped: false,
    canMoveUp: false,
    canMoveDown: false,
    moveUpTargets: [],
    moveDownTargets: [],
    sessionCount: 0,
    sessions: [],
  };
}

describe("ProjectGroup", () => {
  function mountProjectGroup(props: Record<string, unknown> = {}) {
    return mount(ProjectGroup, {
      props: {
        project: createProjectGroup(),
        expanded: true,
        activeSessionId: null,
        activeDragSessionId: null,
        activeDragProjectId: null,
        ...props,
      },
      global: {
        stubs: {
          ContextMenu: { template: "<div><slot /></div>" },
          ContextMenuContent: { template: "<div><slot /></div>" },
          ContextMenuItem: { template: "<button><slot /></button>" },
          ContextMenuSeparator: { template: "<div />" },
          ContextMenuShortcut: { template: "<span><slot /></span>" },
          ContextMenuTrigger: { template: "<div><slot /></div>" },
          ConfirmDeleteProjectDialog: { template: "<div />" },
          InlineEdit: { template: "<div />" },
          SessionItem: {
            props: ["session", "kindLabel", "runningCount", "hasChildren", "childrenExpanded", "active"],
            emits: ["toggleChildren"],
            template: `<div class='session-stub' :data-id='session.session.id' :data-kind='kindLabel' :data-running='runningCount'
              :data-expanded='hasChildren ? String(childrenExpanded) : undefined' @click="$emit('toggleChildren')">{{ session.session.title }}</div>`,
          },
        },
      },
    });
  }

  it("ignores drops without app-local active drag state", async () => {
    const wrapper = mountProjectGroup();

    await wrapper.get("section").trigger("drop", {
      preventDefault: () => {},
      dataTransfer: {
        getData: (type: string) => type === "application/weave-session-id" ? "session-1" : "project-1",
      },
    });

    expect(wrapper.emitted("moveSession")).toBeUndefined();
  });

  it("emits moveSession for trusted active drag state", async () => {
    const wrapper = mountProjectGroup({
      activeDragSessionId: "session-1",
      activeDragProjectId: "project-1",
    });

    await wrapper.get("section").trigger("drop", {
      preventDefault: () => {},
    });

    expect(wrapper.emitted("moveSession")).toEqual([["session-1", "project-2"]]);
  });

  it("treats same-project drop as a no-op", async () => {
    const wrapper = mountProjectGroup({
      project: {
        ...createProjectGroup(),
        projectId: "project-1",
      },
      activeDragSessionId: "session-1",
      activeDragProjectId: "project-1",
    });

    await wrapper.get("section").trigger("drop", {
      preventDefault: () => {},
    });

    expect(wrapper.emitted("moveSession")).toBeUndefined();
  });

  describe("draft row", () => {
    const draft = { key: "new-session-draft-1", title: "Fix the login redirect", projectId: "project-2", isStarting: false };
    const session = {
      instanceId: "instance-1",
      workspaceId: "workspace-1",
      workspaceDirectory: "/repo",
      workspaceDisplayName: null,
      isolationStrategy: "worktree",
      sessionStatus: "active",
      session: { id: "session-1", title: "Fix the login redirect", time: { created: 1, updated: 1 }, tags: [] },
      instanceStatus: "running",
      lifecycleStatus: "running",
      retentionStatus: "active",
      typedInstanceStatus: "running",
      isHidden: false,
      tags: [],
    };

    it("sits above the group's sessions and opens the draft", async () => {
      const wrapper = mountProjectGroup({
        draft,
        draftActive: true,
        project: { ...createProjectGroup(), sessions: [{ ...session, session: { ...session.session, id: "older", title: "Older" } }] },
      });

      const rows = wrapper.findAll(".project-row");
      expect(rows[0].get("[data-testid='new-session-draft-row']").text()).toContain("Fix the login redirect");
      expect(rows[0].text()).toContain("Draft");
      expect(rows[0].get("button").attributes("aria-current")).toBe("true");
      expect(rows[1].text()).toContain("Older");

      await rows[0].get("button").trigger("click");
      expect(wrapper.emitted("openDraft")).toHaveLength(1);
    });

    it("says New session until something is typed, and Starting… once sent", () => {
      expect(mountProjectGroup({ draft: { ...draft, title: "" } }).text()).toContain("New session");
      expect(mountProjectGroup({ draft: { ...draft, isStarting: true } }).text()).toContain("Starting…");
    });

    it("becomes the session's row in place: the same row, not one leaving and one arriving", async () => {
      const wrapper = mountProjectGroup({ draft });
      const draftRow = wrapper.get(".project-row").element;

      await wrapper.setProps({
        draft: null,
        project: { ...createProjectGroup(), sessionCount: 1, sessions: [session] },
        rowKeys: { "session-1": draft.key },
      });
      await flushPromises();

      const rows = wrapper.findAll(".project-row");
      expect(rows).toHaveLength(1);
      expect(rows[0].element).toBe(draftRow);
      expect(rows[0].text()).toContain("Fix the login redirect");
      expect(rows[0].find("[data-testid='new-session-draft-row']").exists()).toBe(false);
    });
  });

  describe("lineage", () => {
    function listItem(id: string, title: string, extra: Record<string, unknown> = {}) {
      return {
        instanceId: `i-${id}`, workspaceId: "w", workspaceDirectory: "/repo", workspaceDisplayName: null, isolationStrategy: "existing",
        sessionStatus: "idle", session: { id, title, time: { created: 1, updated: 1 }, tags: [] }, instanceStatus: "running",
        lifecycleStatus: "running", retentionStatus: "active", typedInstanceStatus: "running", isHidden: false, tags: [], ...extra,
      };
    }

    const subagent = toRunningWorkItem({
      id: "w-1", sessionId: "parent", workId: "call_1", kind: "subagent", title: "code-reviewer", label: "Review the diff",
      status: "running", childSessionId: "child-1", canStop: true, startedAt: "2026-10-04T10:00:00Z",
    }) as RunningWorkItem;

    function mountWithLineage(props: Record<string, unknown> = {}) {
      const sessions = [
        listItem("parent", "Capture Claude Code subagents"),
        listItem("fork", "Fork: emit jobs over SignalR", { forkedFromSessionId: "parent", spawnKind: "fork", sessionStatus: "waiting_input" }),
        listItem("started", "Fix Pi model switch", { spawnedBySessionId: "parent", spawnKind: "api" }),
        listItem("quiet", "Fleet MCP server spike"),
        listItem("quiet-fork", "Try the other approach", { forkedFromSessionId: "quiet", spawnKind: "fork" }),
      ];
      return mountProjectGroup({
        project: { ...createProjectGroup(), sessions, sessionCount: sessions.length },
        runningCounts: new Map([["parent", 3]]),
        runningSubagents: new Map([["parent", [subagent]]]),
        ...props,
      });
    }

    it("nests running subagents, forks and started sessions under their parent with a kind label and a running chip", () => {
      const wrapper = mountWithLineage();

      const topLevel = wrapper.findAll(".project-row > .session-stub").map((row) => row.attributes("data-id"));
      expect(topLevel).toEqual(["parent", "quiet"]);

      const parent = wrapper.get(".session-stub[data-id='parent']");
      expect(parent.attributes("data-running")).toBe("3");
      // Something under it works or waits on you, so its children show.
      expect(parent.attributes("data-expanded")).toBe("true");

      const children = wrapper.findAll("[data-testid='session-children']")[0]!;
      const subagentRow = children.get("[data-testid='subagent-session-row']");
      expect(subagentRow.text()).toContain("Review the diff");
      expect(subagentRow.text()).toContain("subagent");
      expect(children.findAll(".session-stub").map((row) => [row.attributes("data-id"), row.attributes("data-kind")])).toEqual([
        ["fork", "fork"],
        ["started", "started"],
      ]);
    });

    it("folds the children of a quiet parent until it's opened", async () => {
      const wrapper = mountWithLineage();
      const quiet = wrapper.get(".session-stub[data-id='quiet']");

      expect(quiet.attributes("data-expanded")).toBe("false");
      expect(wrapper.find(".session-stub[data-id='quiet-fork']").exists()).toBe(false);

      await quiet.trigger("click");
      expect(wrapper.get(".session-stub[data-id='quiet']").attributes("data-expanded")).toBe("true");
      expect(wrapper.get(".session-stub[data-id='quiet-fork']").attributes("data-kind")).toBe("fork");
    });

    it("opens a quiet parent's children while one of them is the open session", () => {
      const wrapper = mountWithLineage({ activeSessionId: "quiet-fork" });

      expect(wrapper.get(".session-stub[data-id='quiet']").attributes("data-expanded")).toBe("true");
    });

    it("shows a nested session's own running subagents right after it", () => {
      const forkSubagent = { ...subagent, id: "w-2", sessionId: "quiet-fork", childSessionId: "child-2", label: "Check the fork" };
      const wrapper = mountWithLineage({ runningSubagents: new Map([["quiet-fork", [forkSubagent]]]) });

      // A subagent under it works, so the quiet parent opens by itself.
      expect(wrapper.get(".session-stub[data-id='quiet']").attributes("data-expanded")).toBe("true");
      const children = wrapper.findAll("[data-testid='session-children']").at(-1)!;
      const order = children.findAll(".session-stub, [data-testid='subagent-session-row']")
        .map((row) => row.attributes("data-id") ?? row.attributes("data-child-session-id"));
      expect(order).toEqual(["quiet-fork", "child-2"]);
    });

    it("nests a fork of a started session one step further in, under that session", () => {
      const wrapper = mountWithLineage({
        project: {
          ...createProjectGroup(),
          sessions: [
            listItem("parent", "What can we learn from t3code?"),
            listItem("capture", "Capture Claude Code subagents", { spawnedBySessionId: "parent", spawnKind: "api", sessionStatus: "active" }),
            listItem("fork", "Fork: emit jobs over SignalR", { forkedFromSessionId: "capture", spawnKind: "fork" }),
          ],
        },
        runningSubagents: new Map([["capture", [{ ...subagent, sessionId: "capture" }]]]),
      });

      const rows = wrapper.get("[data-testid='session-children']").findAll(".session-child")
        .map((row) => {
          const stub = row.find(".session-stub");
          return [row.attributes("data-depth"), stub.exists() ? stub.attributes("data-id") : "subagent"];
        });
      expect(rows).toEqual([["1", "capture"], ["2", "subagent"], ["2", "fork"]]);
    });

    it("a running subagent's row opens its session under its parent", async () => {
      const wrapper = mountWithLineage();
      await wrapper.get("[data-testid='subagent-session-row']").trigger("click");

      expect(navigate).toHaveBeenCalledWith({
        to: "/sessions/$id", params: { id: "child-1" }, search: { instanceId: "child-1", parentSessionId: "parent" },
      });
    });
  });
});
