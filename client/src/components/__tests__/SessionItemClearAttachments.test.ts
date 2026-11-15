import { enableAutoUnmount, flushPromises, mount } from "@vue/test-utils";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

const { clearSessionAttachments, deleteSession } = vi.hoisted(() => ({ clearSessionAttachments: vi.fn(), deleteSession: vi.fn() }));
vi.mock("@/api/session-attachments", () => ({ clearSessionAttachments }));
vi.mock("@/composables/use-session-actions", async (importOriginal) => {
  const { shallowRef } = await import("vue");
  return {
    ...(await importOriginal<typeof import("@/composables/use-session-actions")>()),
    useDeleteSession: () => ({ deleteSession, isDeleting: shallowRef(false), error: shallowRef(undefined) }),
  };
});
vi.mock("@tanstack/vue-router", () => ({ useRouter: () => ({ navigate: vi.fn() }) }));

import SessionItem from "@/components/sessions/SessionItem.vue";
import type { SessionListItem } from "@/api/client";
import { ConfirmHost } from "@/components/ui/confirm";

enableAutoUnmount(afterEach);

/**
 * The confirm's host, mounted after the item: test-utils applies the last mount's stubs to every app, and the item's
 * would stub out the host's dialog.
 */
function mountHost(): void {
  mount(ConfirmHost, { attachTo: document.body, global: { stubs: { teleport: false } } });
}

/** Lets the confirm open (reka places it and its focus a tick later). */
async function settle(): Promise<void> {
  await flushPromises();
  await new Promise((resolve) => setTimeout(resolve, 0));
  await flushPromises();
}

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

  function dialog(): HTMLElement | null {
    return document.body.querySelector<HTMLElement>("[data-testid='confirm-dialog']");
  }
  function action(): HTMLButtonElement {
    return document.body.querySelector<HTMLButtonElement>("[data-testid='confirm-action']")!;
  }

  it("asks first, then clears the session's attachments, archived or not", async () => {
    clearSessionAttachments.mockResolvedValue(undefined);
    const wrapper = item({ retentionStatus: "archived" });
    mountHost();

    await wrapper.get(entry).trigger("click");
    await settle();
    expect(clearSessionAttachments).not.toHaveBeenCalled();
    expect(dialog()?.textContent).toContain("Clear attachments?");
    expect(dialog()?.textContent).toContain("Fix the login bug");
    expect(action().textContent?.trim()).toBe("Clear attachments");
    action().click();
    await settle();

    expect(clearSessionAttachments).toHaveBeenCalledWith(expect.anything(), "session-1");
    await flushPromises();
    expect(dialog()).toBeNull();
  });

  it("keeps the dialog open and says why when the server refuses", async () => {
    clearSessionAttachments.mockRejectedValue(new Error("The session's folder is gone."));
    const wrapper = item();
    mountHost();

    await wrapper.get(entry).trigger("click");
    await settle();
    action().click();
    await settle();

    expect(dialog()?.textContent).toContain("Couldn't clear the attachments.");
    expect(dialog()?.textContent).toContain("The session's folder is gone.");
  });
});

describe("SessionItem: Delete permanently…", () => {
  function dialog(): HTMLElement | null {
    return document.body.querySelector<HTMLElement>("[data-testid='confirm-dialog']");
  }
  function action(): HTMLButtonElement {
    return document.body.querySelector<HTMLButtonElement>("[data-testid='confirm-action']")!;
  }
  async function chooseDelete(wrapper: ReturnType<typeof item>): Promise<void> {
    await wrapper.findAll("button").find((button) => button.text().includes("Delete permanently"))!.trigger("click");
    await settle();
  }

  beforeEach(() => {
    deleteSession.mockReset();
  });

  it("asks first, naming the session, then deletes it", async () => {
    deleteSession.mockResolvedValue(undefined);
    const wrapper = item();
    mountHost();

    await chooseDelete(wrapper);
    expect(deleteSession).not.toHaveBeenCalled();
    expect(dialog()?.textContent).toContain("Delete this session?");
    expect(dialog()?.textContent).toContain("Fix the login bug");
    expect(document.activeElement?.getAttribute("data-testid")).toBe("confirm-cancel");
    expect(action().textContent?.trim()).toBe("Delete session");
    action().click();
    await settle();

    expect(deleteSession).toHaveBeenCalledWith("session-1", "instance-1");
    expect(dialog()).toBeNull();
  });

  it("keeps the dialog open and says why when the delete fails", async () => {
    deleteSession.mockRejectedValue(new Error("The session is still starting."));
    const wrapper = item();
    mountHost();

    await chooseDelete(wrapper);
    action().click();
    await settle();

    expect(dialog()?.textContent).toContain("Couldn't delete the session.");
    expect(dialog()?.textContent).toContain("The session is still starting.");
  });
});
