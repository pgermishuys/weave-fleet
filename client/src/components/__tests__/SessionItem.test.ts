import { mount } from "@vue/test-utils";
import { describe, expect, it, vi } from "vitest";
import { useArchiveQueueStore } from "@/stores/archive-queue";
import { useLineageMovesStore } from "@/stores/lineage-moves";
import { useSessionsStore } from "@/stores/sessions";
import { useSessionSelectionStore } from "@/stores/session-selection";
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
  it("shows a progress ring and count instead of the status word while working", () => {
    const wrapper = mountSessionItem(createSession({
      progress: { sessionId: "session-1", kind: "todos", done: 3, total: 7, current: "Drop the indexes" },
    }));

    expect(wrapper.find(".progress-ring").exists()).toBe(true);
    expect(wrapper.get(".session-progress__count").text()).toBe("3/7");
    expect(wrapper.find(".session-meta").exists()).toBe(false);
    expect(wrapper.get(".session-progress").attributes("title")).toBe("3 of 7 done. Now: Drop the indexes");
  });

  it("keeps words the user has to act on next to the ring", () => {
    const wrapper = mountSessionItem(createSession({
      sessionStatus: "waiting_input",
      progress: { sessionId: "session-1", kind: "todos", done: 5, total: 8, current: null },
    }));

    expect(wrapper.find(".progress-ring").exists()).toBe(true);
    expect(wrapper.find(".session-progress__count").exists()).toBe(false);
    expect(wrapper.get(".session-meta").text()).toBe("Needs input");
  });

  it("shows no ring for a session without progress or with an empty list", () => {
    const withoutProgress = mountSessionItem(createSession());
    const emptyList = mountSessionItem(createSession({
      progress: { sessionId: "session-1", kind: "todos", done: 0, total: 0, current: null },
    }));

    // A working session without progress shows no word either: its glyph says it's working.
    for (const wrapper of [withoutProgress, emptyList]) {
      expect(wrapper.find(".progress-ring").exists()).toBe(false);
      expect(wrapper.find(".session-progress__count").exists()).toBe(false);
      expect(wrapper.find(".session-meta").exists()).toBe(false);
    }
  });

  it("keeps the ring but shows the age instead of the count for a quiet session with unfinished items", () => {
    const wrapper = mountSessionItem(createSession({
      sessionStatus: "idle",
      activityStatus: "idle",
      progress: { sessionId: "session-1", kind: "plan", done: 11, total: 17, current: "Add migration" },
    }));

    expect(wrapper.find(".progress-ring").exists()).toBe(true);
    expect(wrapper.find(".session-progress__count").exists()).toBe(false);
    expect(wrapper.get(".session-progress").attributes("title")).toBe("11 of 17 done. Now: Add migration");
    expect(wrapper.get(".session-meta").classes()).toContain("session-meta--quiet");
    expect(wrapper.get(".session-meta").text()).not.toBe("");
  });

  it("shows no progress once a quiet session has finished its list, and its status label comes back", () => {
    const idle = mountSessionItem(createSession({
      sessionStatus: "idle",
      activityStatus: "idle",
      progress: { sessionId: "session-1", kind: "todos", done: 2, total: 2, current: null },
    }));
    const completed = mountSessionItem(createSession({
      sessionStatus: "completed",
      activityStatus: "idle",
      progress: { sessionId: "session-1", kind: "plan", done: 5, total: 5, current: null },
    }));

    for (const wrapper of [idle, completed]) {
      expect(wrapper.find(".session-progress").exists()).toBe(false);
      expect(wrapper.find(".progress-ring").exists()).toBe(false);
      expect(wrapper.find(".session-progress__count").exists()).toBe(false);
      expect(wrapper.get(".session-meta").classes()).toContain("session-meta--quiet");
    }
    expect(idle.get(".session-meta").text()).not.toBe("");
    expect(completed.get(".session-meta").text()).toBe("Done");
  });

  it("still shows the count while a session works through its last item", () => {
    const wrapper = mountSessionItem(createSession({
      progress: { sessionId: "session-1", kind: "todos", done: 2, total: 2, current: null },
    }));

    expect(wrapper.find(".progress-ring").exists()).toBe(true);
    expect(wrapper.get(".session-progress__count").text()).toBe("2/2");
    expect(wrapper.find(".session-meta").exists()).toBe(false);
  });

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
    expect(text).toContain("Archive");
    expect(text).not.toContain("Restore");
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
    expect(text).not.toContain("Archive");
    expect(text).not.toContain("Permanently Delete");
  });

  it("says_what_fork_does", () => {
    const wrapper = mountSessionItem(createSession());

    const fork = wrapper.get("[data-testid='session-context-fork']");
    expect(fork.attributes("disabled")).toBeUndefined();
    expect(wrapper.get("[data-testid='session-context-fork-note']").text()).toBe("A new session with a copy of this conversation");
  });

  it("shows_fork_off_with_the_reason_on_a_harness_that_cant_fork", () => {
    const reason = "Claude Code can't copy a conversation, so its sessions can't be forked.";
    const wrapper = mountSessionItem(createSession({
      capabilities: createCapabilities({ canFork: false, forkDisabledReason: reason }),
    }));

    expect(wrapper.get("[data-testid='session-context-fork']").attributes("disabled")).toBeDefined();
    expect(wrapper.get("[data-testid='session-context-fork-note']").text()).toBe(reason);
  });

  it("offers_a_new_session_in_the_folder_even_where_fork_is_off", () => {
    const wrapper = mountSessionItem(createSession({
      capabilities: createCapabilities({ canFork: false, forkDisabledReason: "Claude Code can't copy a conversation, so its sessions can't be forked." }),
    }));

    const item = wrapper.get("[data-testid='session-context-new-in-folder']");
    expect(item.attributes("disabled")).toBeUndefined();
    expect(item.text()).toContain("New session in this folder");
  });

  it("has_no_fork_on_an_archived_session", () => {
    const wrapper = mountSessionItem(createSession({
      retentionStatus: "archived",
      capabilities: createCapabilities({ canFork: false, forkDisabledReason: "Archived sessions are read-only." }),
    }));

    expect(wrapper.find("[data-testid='session-context-fork']").exists()).toBe(false);
    expect(wrapper.find("[data-testid='session-context-new-in-folder']").exists()).toBe(false);
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

  it("archives_from_the_hover_button_through_the_undo_queue", async () => {
    const wrapper = mountSessionItem(createSession());
    const queue = useArchiveQueueStore();

    await wrapper.get("[data-testid='session-row-archive']").trigger("click");

    expect(queue.pending?.ids).toEqual(["session-1"]);
    expect(wrapper.emitted("select")).toBeUndefined();
    queue.undo();
  });

  it("offers_restore_instead_of_archive_for_an_archived_session", () => {
    const wrapper = mountSessionItem(createSession({
      retentionStatus: "archived",
      capabilities: createCapabilities({ canArchive: false, canUnarchive: true }),
    }));

    expect(wrapper.find("[data-testid='session-row-archive']").exists()).toBe(false);
    expect(wrapper.find("[data-testid='session-row-restore']").exists()).toBe(true);
    expect(wrapper.get("[data-testid='context-menu-content']").text()).toContain("Restore");
  });

  it("picks_the_row_on_ctrl_click_instead_of_opening_it", async () => {
    const wrapper = mountSessionItem(createSession());
    const selection = useSessionSelectionStore();

    await wrapper.get("[data-testid='session-row']").trigger("click", { ctrlKey: true });

    expect(selection.isSelected("session-1")).toBe(true);
    expect(wrapper.emitted("select")).toBeUndefined();
  });

  it("opens_the_session_on_a_plain_click", async () => {
    const wrapper = mountSessionItem(createSession());

    await wrapper.get("[data-testid='session-row']").trigger("click");

    expect(wrapper.emitted("select")).toHaveLength(1);
  });

  it("renames_in_place_on_double_click", async () => {
    const wrapper = mountSessionItem(createSession());

    await wrapper.get("[data-testid='session-row']").trigger("dblclick");

    expect(wrapper.find("input[aria-label='Session name']").exists()).toBe(true);
  });

  describe("lineage", () => {
    function mountWith(props: Record<string, unknown>) {
      return mount(SessionItem, {
        props: { active: false, session: createSession({ sessionStatus: "idle", activityStatus: "idle" }), ...props },
        global: { stubs: contextMenuStubs },
      });
    }

    it("shows a green chip counting the work its agent has running", () => {
      const wrapper = mountWith({ runningCount: 3 });
      const chip = wrapper.get("[data-testid='session-running-chip']");

      expect(chip.text()).toContain("3");
      expect(chip.attributes("title")).toBe("3 things running in the background");
      expect(mountWith({ runningCount: 0 }).find("[data-testid='session-running-chip']").exists()).toBe(false);
    });

    it("under its parent, says how it came from it instead of its status", () => {
      const wrapper = mountWith({ kindLabel: "fork" });

      expect(wrapper.get("[data-testid='session-kind']").text()).toBe("fork");
      expect(wrapper.find(".session-meta").exists()).toBe(false);
    });

    describe("moving it out of its parent", () => {
      function menuText(session: SessionListItem): string {
        return mountWith({ session }).get("[data-testid='context-menu-content']").text();
      }

      it("names the session it really came from, which may not be the row it sits under", async () => {
        useSessionsStore().setSessions([createSession({ session: { id: "capture", title: "Capture subagents", time: { created: 1, updated: 1 }, tags: [] } })]);
        const fork = createSession({ forkedFromSessionId: "capture", spawnKind: "fork" });
        const wrapper = mountWith({ session: fork, kindLabel: "fork" });
        const moveOut = vi.spyOn(useLineageMovesStore(), "moveOut").mockResolvedValue();

        const item = wrapper.get("[data-testid='session-context-move-out']");
        expect(item.text()).toBe('Move out of "Capture subagents"');
        await item.trigger("click");

        expect(moveOut).toHaveBeenCalledWith("session-1");
        expect(wrapper.find("[data-testid='session-context-move-back']").exists()).toBe(false);
      });

      it("is there for a session an agent started, not for a subagent's session or one the user started", () => {
        expect(menuText(createSession({ spawnedBySessionId: "elsewhere", spawnKind: "api" }))).toContain("Move out of its parent");
        expect(menuText(createSession({ parentSessionId: "parent" }))).not.toContain("Move out");
        expect(menuText(createSession())).not.toContain("Move out");
        expect(menuText(createSession({ retentionStatus: "archived", forkedFromSessionId: "a", spawnKind: "fork" }))).not.toContain("Move out");
      });

      it("offers to move a session that's out back under its parent", async () => {
        const out = createSession({ forkedFromSessionId: "elsewhere", spawnKind: "fork", lineageDetachedAt: "2026-10-04T12:00:00Z" });
        const wrapper = mountWith({ session: out });
        const moveBack = vi.spyOn(useLineageMovesStore(), "moveBack").mockResolvedValue();

        expect(wrapper.find("[data-testid='session-context-move-out']").exists()).toBe(false);
        await wrapper.get("[data-testid='session-context-move-back']").trigger("click");

        expect(moveBack).toHaveBeenCalledWith("session-1");
      });
    });

    it("a parent opens and closes its children with the caret or the arrow keys, without opening the session", async () => {
      const wrapper = mountWith({ hasChildren: true, childrenExpanded: false });
      const row = wrapper.get("[data-testid='session-row']");

      expect(row.attributes("aria-expanded")).toBe("false");
      await wrapper.get("[data-testid='session-children-toggle']").trigger("click");
      expect(wrapper.emitted("toggleChildren")).toHaveLength(1);
      expect(wrapper.emitted("select")).toBeUndefined();

      await row.trigger("keydown", { key: "ArrowLeft" });
      expect(wrapper.emitted("toggleChildren")).toHaveLength(1);
      await row.trigger("keydown", { key: "ArrowRight" });
      expect(wrapper.emitted("toggleChildren")).toHaveLength(2);

      expect(mountWith({}).get("[data-testid='session-row']").attributes("aria-expanded")).toBeUndefined();
    });
  });
});
