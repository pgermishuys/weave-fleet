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
    expect(wrapper.get(".session-meta").text()).toBe("Active");
    expect(wrapper.get("button").attributes("aria-current")).toBe("true");
    expect(wrapper.get("button").classes()).toContain("active");
    expect(wrapper.get(".session-dot").attributes("style")).toContain("background-color: var(--running)");
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
    expect(wrapper.get(".session-meta").text()).toBe("Completed");
  });
});
