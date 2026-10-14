import { flushPromises, mount } from "@vue/test-utils";
import { beforeEach, describe, expect, it, vi } from "vitest";

const { clearSessionAttachments } = vi.hoisted(() => ({ clearSessionAttachments: vi.fn() }));
vi.mock("@/api/session-attachments", () => ({ clearSessionAttachments }));
vi.mock("@tanstack/vue-router", () => ({ useRouter: () => ({ navigate: vi.fn() }) }));

import SessionItem from "@/components/sessions/SessionItem.vue";
import type { SessionListItem } from "@/api/client";

const stubs = {
  ContextMenu: { template: "<div><slot /></div>" },
  ContextMenuContent: { template: "<div><slot /></div>" },
  ContextMenuItem: {
    props: ["disabled", "variant", "hint"],
    emits: ["select"],
    template: "<button type=\"button\" :disabled=\"disabled\" :data-hint=\"hint\" @click=\"$emit('select', $event)\"><slot /></button>",
  },
  ContextMenuSeparator: { template: "<hr>" },
  ContextMenuSub: { template: "<div><slot /></div>" },
  ContextMenuSubContent: { template: "<div><slot /></div>" },
  ContextMenuSubTrigger: { template: "<button type=\"button\"><slot /></button>" },
  ContextMenuTrigger: { template: "<div><slot /></div>" },
  ConfirmDeleteSessionDialog: { template: "<div />" },
  ConfirmClearAttachmentsDialog: {
    props: ["open", "error", "isClearing"],
    emits: ["confirm"],
    template: "<div v-if=\"open\" data-testid=\"clear-dialog\"><button data-testid=\"clear-confirm\" @click=\"$emit('confirm')\" /><p>{{ error }}</p></div>",
  },
  OpenToolContextSubmenu: { template: "<div />" },
};

function session(harnessType: string, overrides: Partial<SessionListItem> = {}): SessionListItem {
  return {
    instanceId: "instance-1",
    workspaceId: "workspace-1",
    workspaceDirectory: "/work/api",
    workspaceDisplayName: "api",
    isolationStrategy: "existing",
    sessionStatus: "idle",
    session: { id: "session-1", title: "Fix the login bug", time: { created: 1, updated: 2 }, tags: [] },
    instanceStatus: "running",
    parentSessionId: null,
    sourceDirectory: "/work/api",
    branch: "main",
    activityStatus: "idle",
    lifecycleStatus: "running",
    retentionStatus: "active",
    archivedAt: null,
    typedInstanceStatus: "running",
    isHidden: false,
    harnessType,
    tags: [],
    ...overrides,
  } as SessionListItem;
}

function item(overrides: Partial<SessionListItem> = {}) {
  return mount(SessionItem, { props: { active: false, session: session("opencode", overrides) }, global: { stubs } });
}

describe("SessionItem: Clear attachments…", () => {
  const entry = "[data-testid='session-context-clear-attachments']";

  beforeEach(() => {
    clearSessionAttachments.mockReset();
  });

  it("is in the menu", () => {
    expect(item().find(entry).exists()).toBe(true);
  });

  it("asks first, then clears the session's attachments, archived or not", async () => {
    clearSessionAttachments.mockResolvedValue(undefined);
    const wrapper = item({ retentionStatus: "archived" });

    await wrapper.get(entry).trigger("click");
    expect(clearSessionAttachments).not.toHaveBeenCalled();
    await wrapper.get("[data-testid='clear-confirm']").trigger("click");
    await flushPromises();

    expect(clearSessionAttachments).toHaveBeenCalledWith(expect.anything(), "session-1");
    expect(wrapper.find("[data-testid='clear-dialog']").exists()).toBe(false);
  });

  it("keeps the dialog open and says why when the server refuses", async () => {
    clearSessionAttachments.mockRejectedValue(new Error("The session's folder is gone."));
    const wrapper = item();

    await wrapper.get(entry).trigger("click");
    await wrapper.get("[data-testid='clear-confirm']").trigger("click");
    await flushPromises();

    expect(wrapper.get("[data-testid='clear-dialog']").text()).toContain("The session's folder is gone.");
  });
});
