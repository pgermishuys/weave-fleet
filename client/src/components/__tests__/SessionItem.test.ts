import { mount } from "@vue/test-utils";
import { describe, expect, it } from "vitest";
import SessionItem from "@/components/sessions/SessionItem.vue";
import type { SessionListItem } from "@/api/client";

function createCapabilities(overrides: Partial<NonNullable<SessionListItem["capabilities"]>> = {}): NonNullable<SessionListItem["capabilities"]> {
  return {
    canPrompt: true,
    canRestart: false,
    canAbort: false,
    canArchive: true,
    canUnarchive: false,
    canFork: true,
    canDelete: true,
    promptDisabledReason: null,
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
      tags: [],
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
    tags: [],
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
    expect(wrapper.get(".status-glyph").attributes("aria-label")).toBe("Working");
    expect(wrapper.find(".session-meta").exists()).toBe(false);
  });

  it("names delegation and retries on the glyph, and shows the retry attempt", () => {
    const delegating = mountSessionItem(createSession({ activityStatus: "delegating" }));
    expect(delegating.get(".status-glyph").attributes("aria-label")).toBe("Delegating");

    const retrying = mountSessionItem(createSession({ activityStatus: "retry", retryAttempt: 2 }));
    expect(retrying.get(".status-glyph").classes()).toContain("status-glyph--retry");
    expect(retrying.get(".status-glyph").attributes("aria-label")).toBe("Retrying (attempt 2)");
    expect(retrying.get(".session-meta").text()).toBe("Retry 2");
  });

  it("shows no status glyph on an idle row, and keeps its slot so titles line up", () => {
    const wrapper = mountSessionItem(createSession({ sessionStatus: "idle", activityStatus: null }));

    expect(wrapper.find(".status-glyph").exists()).toBe(false);
    expect(wrapper.find(".session-glyph-slot").exists()).toBe(true);
    expect(wrapper.get(".session-meta").text()).not.toBe("");
  });

  it("keeps the glyph for sessions that are waiting or stopped by an error", () => {
    expect(mountSessionItem(createSession({ sessionStatus: "waiting_input" })).find(".status-glyph").exists()).toBe(true);
    expect(mountSessionItem(createSession({ sessionStatus: "error" })).find(".status-glyph").exists()).toBe(true);
  });

  it("dims a quiet row by its last activity, but not a live or open one", () => {
    const DAY = 24 * 60 * 60_000;
    const quiet = (updated: number) => createSession({
      sessionStatus: "idle",
      activityStatus: null,
      session: { id: "session-1", title: "Fix auth bug", time: { created: updated, updated }, tags: [] },
    } as Partial<SessionListItem>);

    expect(mountSessionItem(quiet(Date.now() - 60_000)).get("button").classes()).not.toContain("session-item--dim-1");
    expect(mountSessionItem(quiet(Date.now() - 2 * DAY)).get("button").classes()).toContain("session-item--dim-1");
    expect(mountSessionItem(quiet(Date.now() - 4 * DAY)).get("button").classes()).toContain("session-item--dim-2");

    const open = mountSessionItem(quiet(Date.now() - 4 * DAY), true);
    expect(open.get("button").classes()).not.toContain("session-item--dim-2");

    // createSession's default is an active session with ancient timestamps.
    expect(mountSessionItem(createSession()).get("button").classes().some((c) => c.startsWith("session-item--dim"))).toBe(false);
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
        tags: [],
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
        canArchive: true,
        canFork: true,
        canDelete: true,
      }),
    }));

    const text = wrapper.get("[data-testid='context-menu-content']").text();
    expect(text).toContain("Complete");
    expect(text).toContain("Fork");
    expect(text).toContain("Permanently Delete");
  });

  it("hides_context_actions_disabled_by_session_capabilities", () => {
    const wrapper = mountSessionItem(createSession({
      capabilities: createCapabilities({
        canArchive: false,
        canFork: false,
        canDelete: false,
      }),
    }));

    const text = wrapper.get("[data-testid='context-menu-content']").text();
    expect(text).not.toContain("Complete");
    expect(text).not.toContain("Fork");
    expect(text).not.toContain("Permanently Delete");
  });

  it.each(["running", "stopped"] as const)("never_offers_pause_or_resume_for_a_%s_session", (lifecycleStatus) => {
    const wrapper = mountSessionItem(createSession({
      sessionStatus: lifecycleStatus === "running" ? "active" : "stopped",
      lifecycleStatus,
    }));

    const text = wrapper.get("[data-testid='context-menu-content']").text();
    expect(text).not.toContain("Pause");
    expect(text).not.toContain("Resume");
  });
});
