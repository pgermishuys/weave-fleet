import { mount } from "@vue/test-utils";
import { describe, expect, it } from "vitest";
import SessionItem from "@/components/sessions/SessionItem.vue";
import type { SessionListItem } from "@/lib/api-types";

function createCapabilities(overrides: Partial<NonNullable<SessionListItem["capabilities"]>> = {}): NonNullable<SessionListItem["capabilities"]> {
  return {
    canPrompt: true,
    canStop: true,
    canResume: false,
    canRestart: false,
    canAbort: false,
    canArchive: true,
    canUnarchive: false,
    canFork: true,
    canDelete: true,
    promptDisabledReason: null,
    stopDisabledReason: null,
    resumeDisabledReason: null,
    restartDisabledReason: null,
    abortDisabledReason: null,
    archiveDisabledReason: null,
    unarchiveDisabledReason: null,
    forkDisabledReason: null,
    deleteDisabledReason: null,
    ...overrides,
  };
}

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
    capabilities: createCapabilities(),
    ...overrides,
  };
}

const contextMenuStubs = {
  ContextMenu: {
    props: ["open"],
    emits: ["update:open"],
    template: "<div><slot /></div>",
  },
  ContextMenuContent: {
    template: "<div data-testid=\"context-menu-content\"><slot /></div>",
  },
  ContextMenuItem: {
    props: ["disabled", "variant"],
    emits: ["select"],
    template: "<button type=\"button\" :disabled=\"disabled\" :data-variant=\"variant\" @click=\"$emit('select', $event)\"><slot /></button>",
  },
  ContextMenuSeparator: {
    template: "<hr>",
  },
  ContextMenuSub: {
    template: "<div><slot /></div>",
  },
  ContextMenuSubContent: {
    template: "<div><slot /></div>",
  },
  ContextMenuSubTrigger: {
    props: ["disabled"],
    template: "<button type=\"button\" :disabled=\"disabled\"><slot /></button>",
  },
  ContextMenuTrigger: {
    template: "<div><slot /></div>",
  },
  ConfirmCompleteSessionDialog: {
    template: "<div data-testid=\"confirm-complete-dialog\" />",
  },
  ConfirmDeleteSessionDialog: {
    template: "<div data-testid=\"confirm-delete-dialog\" />",
  },
  OpenToolContextSubmenu: {
    template: "<div data-testid=\"open-tool-submenu\" />",
  },
};

function mountSessionItem(session: SessionListItem, active = false) {
  return mount(SessionItem, {
    props: {
      active,
      session,
    },
    global: {
      stubs: contextMenuStubs,
    },
  });
}

describe("SessionItem", () => {
  it("renders the session title and current status", () => {
    const wrapper = mountSessionItem(createSession(), true);

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
    const wrapper = mountSessionItem(session);

    await wrapper.get("button").trigger("click");

    expect(wrapper.emitted("select")).toEqual([[session]]);
  });

  it("sets draggable to true when not editing and no action is pending", () => {
    const wrapper = mountSessionItem(createSession());

    expect(wrapper.get(".session-item-shell").attributes("draggable")).toBe("true");
  });

  it("sets the correct dataTransfer values on dragstart", async () => {
    const session = createSession({ projectId: "project-1" });
    const wrapper = mountSessionItem(session);

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
    const wrapper = mountSessionItem(createSession());

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

  it("shows_context_actions_enabled_by_session_capabilities", () => {
    const wrapper = mountSessionItem(createSession({
      capabilities: createCapabilities({
        canStop: true,
        canResume: true,
        canArchive: true,
        canFork: true,
        canDelete: true,
      }),
    }));

    const text = wrapper.get("[data-testid='context-menu-content']").text();
    expect(text).toContain("Pause");
    expect(text).toContain("Resume");
    expect(text).toContain("Complete");
    expect(text).toContain("Fork");
    expect(text).toContain("Permanently Delete");
  });

  it("hides_context_actions_disabled_by_session_capabilities", () => {
    const wrapper = mountSessionItem(createSession({
      capabilities: createCapabilities({
        canStop: false,
        canResume: false,
        canArchive: false,
        canFork: false,
        canDelete: false,
      }),
    }));

    const text = wrapper.get("[data-testid='context-menu-content']").text();
    expect(text).not.toContain("Pause");
    expect(text).not.toContain("Resume");
    expect(text).not.toContain("Complete");
    expect(text).not.toContain("Fork");
    expect(text).not.toContain("Permanently Delete");
  });

  it("hides_resume_and_pause_for_stopped_automatic_session_capabilities", () => {
    const wrapper = mountSessionItem(createSession({
      sessionStatus: "stopped",
      instanceStatus: "dead",
      activityStatus: "idle",
      lifecycleStatus: "stopped",
      typedInstanceStatus: "stopped",
      capabilities: createCapabilities({
        canPrompt: true,
        canStop: false,
        canResume: false,
      }),
    }));

    const text = wrapper.get("[data-testid='context-menu-content']").text();
    expect(text).not.toContain("Pause");
    expect(text).not.toContain("Resume");
  });
});
