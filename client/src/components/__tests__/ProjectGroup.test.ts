import { mount } from "@vue/test-utils";
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
          SessionItem: { template: "<div />" },
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
});
