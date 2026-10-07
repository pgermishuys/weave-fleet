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

    it("keeps everything under the top-level session one indent in, in tree order, however deep", () => {
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

      const children = wrapper.get("[data-testid='session-children']");
      const rows = children.findAll(".session-child").map((row) => {
        const stub = row.find(".session-stub");
        return [stub.exists() ? stub.attributes("data-id") : "subagent", stub.exists() ? stub.attributes("data-kind") : null];
      });
      expect(rows).toEqual([["capture", "started"], ["subagent", null], ["fork", "fork"]]);
      // No staircase: no row steps further in than the others.
      expect(children.findAll(".session-child").every((row) => !row.attributes("style"))).toBe(true);
      expect(wrapper.findAll("[data-testid='session-children']")).toHaveLength(1);
    });

    it("nests a subagent's own running subagents (Claude Code nesting) under the same top-level session", () => {
      const nested = { ...subagent, id: "w-3", sessionId: "child-1", childSessionId: "child-3", label: "Read the mapper" };
      const wrapper = mountWithLineage({ runningSubagents: new Map([["parent", [subagent]], ["child-1", [nested]]]) });

      const children = wrapper.findAll("[data-testid='session-children']")[0]!;
      expect(children.findAll("[data-testid='subagent-session-row']").map((row) => row.attributes("data-child-session-id")))
        .toEqual(["child-1", "child-3"]);
    });

    describe("dragging a session out of its parent", () => {
      function mountDragging(sessionId: string, projectId = "project-2") {
        return mountWithLineage({ activeDragSessionId: sessionId, activeDragProjectId: projectId });
      }

      /** Whether the browser would let the session drop here: the handler cancels dragover. */
      async function dragOver(target: { element: Element }): Promise<boolean> {
        const event = new Event("dragover", { bubbles: true, cancelable: true });
        target.element.dispatchEvent(event);
        await flushPromises();
        return event.defaultPrevented;
      }

      it("moves a nested fork out when it's dropped on its project, and shows the project as the place to drop", async () => {
        const wrapper = mountDragging("fork");

        await wrapper.get("section").trigger("dragenter");
        expect(await dragOver(wrapper.get("section"))).toBe(true);
        expect(wrapper.get(".project-header").classes()).toContain("project-header--drop-target");

        await wrapper.get("section").trigger("drop", { preventDefault: () => {} });
        expect(wrapper.emitted("moveOutOfParent")).toEqual([["fork"]]);
        expect(wrapper.emitted("moveSession")).toBeUndefined();
      });

      it("does nothing over its own family", async () => {
        const wrapper = mountDragging("fork");

        await wrapper.get("section").trigger("dragenter");

        expect(await dragOver(wrapper.get(".session-stub[data-id='parent']"))).toBe(false);
        expect(wrapper.get(".project-header").classes()).not.toContain("project-header--drop-target");
      });

      it("doesn't offer a drop for a top-level session in its own project", async () => {
        const wrapper = mountDragging("quiet");

        await wrapper.get("section").trigger("dragenter");
        expect(await dragOver(wrapper.get("section"))).toBe(false);
        await wrapper.get("section").trigger("drop", { preventDefault: () => {} });

        expect(wrapper.get(".project-header").classes()).not.toContain("project-header--drop-target");
        expect(wrapper.emitted("moveOutOfParent")).toBeUndefined();
      });

      it("moves a nested session to another project as before, without moving it out", async () => {
        const wrapper = mountDragging("fork", "project-1");

        await wrapper.get("section").trigger("drop", { preventDefault: () => {} });

        expect(wrapper.emitted("moveSession")).toEqual([["fork", "project-2"]]);
        expect(wrapper.emitted("moveOutOfParent")).toBeUndefined();
      });
    });

    it("a running subagent's row opens its session under its parent", async () => {
      const wrapper = mountWithLineage();
      await wrapper.get("[data-testid='subagent-session-row']").trigger("click");

      expect(navigate).toHaveBeenCalledWith({
        to: "/sessions/$id", params: { id: "child-1" }, search: { instanceId: "child-1", parentSessionId: "parent" },
      });
    });
  });

  describe("the Pinned group", () => {
    function pinnedItem(id: string, pinOrder: number | null) {
      return {
        instanceId: `i-${id}`, workspaceId: "w", workspaceDirectory: "/repo", workspaceDisplayName: null, isolationStrategy: "existing",
        sessionStatus: "idle", session: { id, title: id, time: { created: 1, updated: 1 }, tags: [] }, instanceStatus: "running",
        lifecycleStatus: "running", retentionStatus: "active", typedInstanceStatus: "running", isHidden: false, tags: [], pinOrder,
      };
    }

    function mountPinned(sessions: unknown[], dragging: string) {
      const wrapper = mountProjectGroup({
        pinned: true,
        project: { ...createProjectGroup(), id: "pinned", projectId: null, name: "Pinned", sessions, sessionCount: sessions.length },
        activeDragSessionId: dragging,
        activeDragProjectId: "project-1",
      });
      // Each pinned row is 32px tall, one under the other.
      wrapper.findAll(".project-row[data-family]").forEach((row, index) => {
        row.element.getBoundingClientRect = () => ({ top: index * 32, height: 32, bottom: index * 32 + 32 }) as DOMRect;
      });
      return wrapper;
    }

    async function dragOverAt(target: { element: Element }, clientY: number): Promise<boolean> {
      const event = new MouseEvent("dragover", { bubbles: true, cancelable: true, clientY });
      target.element.dispatchEvent(event);
      await flushPromises();
      return event.defaultPrevented;
    }

    it("has a pin in its header", () => {
      const wrapper = mountPinned([pinnedItem("a", 1)], "");

      expect(wrapper.get("[data-testid='pinned-header']").text()).toContain("Pinned");
      expect(wrapper.find(".project-title__pin").exists()).toBe(true);
    });

    it("shows a line where a dragged session will go, and pins it there", async () => {
      const wrapper = mountPinned([pinnedItem("a", 1), pinnedItem("b", 2)], "c");

      await wrapper.get("section").trigger("dragenter");
      expect(await dragOverAt(wrapper.get("section"), 40)).toBe(true);
      expect(wrapper.get(".project-row[data-family='b']").classes()).toContain("project-row--drop-before");

      await wrapper.get("section").trigger("drop", { clientY: 40 });
      expect(wrapper.emitted("pinSession")).toEqual([["c", "b"]]);
    });

    it("pins at the end when it's dropped below the last pinned session", async () => {
      const wrapper = mountPinned([pinnedItem("a", 1), pinnedItem("b", 2)], "c");

      await wrapper.get("section").trigger("dragenter");
      await dragOverAt(wrapper.get("section"), 100);
      expect(wrapper.get(".project-content").classes()).toContain("project-content--drop-end");

      await wrapper.get("section").trigger("drop", { clientY: 100 });
      expect(wrapper.emitted("pinSession")).toEqual([["c", null]]);
    });

    it("does nothing when a pinned session is dropped where it already is", async () => {
      const wrapper = mountPinned([pinnedItem("a", 1), pinnedItem("b", 2)], "a");

      await wrapper.get("section").trigger("dragenter");
      expect(await dragOverAt(wrapper.get("section"), 40)).toBe(false);

      await wrapper.get("section").trigger("drop", { clientY: 40 });
      expect(wrapper.emitted("pinSession")).toBeUndefined();
    });

    it("offers somewhere to drop while it's empty", async () => {
      const wrapper = mountPinned([], "c");

      expect(wrapper.get("[data-testid='pinned-empty']").text()).toBe("Drop here to pin");
      await wrapper.get("section").trigger("drop", { clientY: 10 });
      expect(wrapper.emitted("pinSession")).toEqual([["c", null]]);
    });

    it("unpins a pinned session dropped on its own project", async () => {
      const wrapper = mountProjectGroup({ activeDragSessionId: "a", activeDragProjectId: "project-2", activeDragPinned: true });

      await wrapper.get("section").trigger("dragenter");
      expect(wrapper.get(".project-header").classes()).toContain("project-header--drop-target");
      await wrapper.get("section").trigger("drop", { preventDefault: () => {} });

      expect(wrapper.emitted("unpinSession")).toEqual([["a"]]);
      expect(wrapper.emitted("moveSession")).toBeUndefined();
    });
  });
});
