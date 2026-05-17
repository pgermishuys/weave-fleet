import { mount } from "@vue/test-utils";
import { describe, expect, it } from "vitest";
import SessionItem from "@/components/sessions/SessionItem.vue";
import type { SessionListItem } from "@/lib/api-types";

function createSession(overrides: Partial<SessionListItem> = {}): SessionListItem {
  return {
    instanceId: "instance-1",
    workspaceId: "workspace-1",
    workspaceDirectory: "/tmp/api",
    workspaceDisplayName: "api",
    isolationStrategy: "existing",
    sessionStatus: "active",
    session: {
      id: "session-1",
      title: "Fix auth bug",
      time: {
        created: 1,
        updated: 2,
      },
    },
    instanceStatus: "running",
    parentSessionId: null,
    sourceDirectory: "/tmp/api",
    branch: "main",
    activityStatus: "busy",
    lifecycleStatus: "running",
    retentionStatus: "active",
    archivedAt: null,
    typedInstanceStatus: "running",
    isHidden: false,
    projectId: "project-1",
    projectName: "Api",
    ...overrides,
  };
}

describe("SessionItem", () => {
  it("renders the session title and current status", () => {
    const wrapper = mount(SessionItem, {
      props: {
        active: true,
        session: createSession(),
      },
    });

    expect(wrapper.get(".session-title").text()).toBe("Fix auth bug");
    expect(wrapper.get("button").attributes("aria-current")).toBe("true");
    expect(wrapper.get("button").classes()).toContain("active");
    expect(wrapper.get(".status-glyph").attributes("aria-label")).toBe("Active");
  });

  it("emits the session when clicked", async () => {
    const session = createSession({
      sessionStatus: "completed",
      session: {
        id: "session-2",
        title: "Write tests",
        time: {
          created: 1,
          updated: 2,
        },
      },
      projectName: null,
      lifecycleStatus: "completed",
      activityStatus: null,
    });
    const wrapper = mount(SessionItem, {
      props: {
        active: false,
        session,
      },
    });

    await wrapper.get("button").trigger("click");

    expect(wrapper.emitted("select")).toEqual([[session]]);
  });

  it("sets draggable to true when not editing and no action is pending", () => {
    const wrapper = mount(SessionItem, {
      props: {
        active: false,
        session: createSession(),
      },
    });

    expect(wrapper.get(".session-item-shell").attributes("draggable")).toBe("true");
  });

  it("sets the correct dataTransfer values on dragstart", async () => {
    const session = createSession({ projectId: "project-1" });
    const wrapper = mount(SessionItem, {
      props: {
        active: false,
        session,
      },
    });

    const dataMap = new Map<string, string>();
    const dataTransfer = {
      effectAllowed: "none",
      setData: (type: string, value: string) => {
        dataMap.set(type, value);
      },
      types: [],
    } as unknown as DataTransfer;

    await wrapper.get(".session-item-shell").trigger("dragstart", { dataTransfer });

    expect(dataMap.get("text/plain")).toBe("session-1");
    expect(dataMap.get("application/weave-session-id")).toBe("session-1");
    expect(dataMap.get("application/weave-source-project-id")).toBe("project-1");
  });

  it("adds the dragging class during drag and removes it on dragend", async () => {
    const wrapper = mount(SessionItem, {
      props: {
        active: false,
        session: createSession(),
      },
    });

    const shell = wrapper.get(".session-item-shell");
    const dataTransfer = {
      effectAllowed: "none",
      setData: () => {},
      types: [],
    } as unknown as DataTransfer;

    await shell.trigger("dragstart", { dataTransfer });
    expect(shell.classes()).toContain("session-item-shell--dragging");

    await shell.trigger("dragend");
    expect(shell.classes()).not.toContain("session-item-shell--dragging");
  });
});
