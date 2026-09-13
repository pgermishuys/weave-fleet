import { flushPromises, mount } from "@vue/test-utils";
import { describe, expect, it } from "vitest";
import ProjectGroup from "@/components/sessions/ProjectGroup.vue";

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
          SessionItem: { props: ["session"], template: "<div class='session-stub'>{{ session.session.title }}</div>" },
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
});
