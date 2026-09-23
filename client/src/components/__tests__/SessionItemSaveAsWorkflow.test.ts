import { mount } from "@vue/test-utils";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { ref } from "vue";

const { navigate, draftFromSession, workflowsOn } = vi.hoisted(() => ({
  navigate: vi.fn(),
  draftFromSession: vi.fn(),
  workflowsOn: { value: true },
}));
vi.mock("@tanstack/vue-router", () => ({ useRouter: () => ({ navigate }) }));
vi.mock("@/composables/use-workflows-feature", () => ({
  useWorkflowsFeature: () => ({ isWorkflowsEnabled: ref(workflowsOn.value) }),
}));
vi.mock("@/composables/use-workflows-nav", () => ({ useWorkflowsNav: () => ({ draftFromSession }) }));
vi.mock("@/composables/use-enabled-harnesses", () => ({
  useEnabledHarnesses: () => ({
    harnesses: ref([
      { type: "opencode", displayName: "OpenCode", capabilities: { supportsOffTheRecordPrompt: true, supportsWorkflowSteps: true } },
      { type: "claude-code", displayName: "Claude Code", capabilities: { supportsOffTheRecordPrompt: false } },
    ]),
  }),
}));

import SessionItem from "@/components/sessions/SessionItem.vue";
import type { SessionListItem } from "@/api/client";

const stubs = {
  ContextMenu: { template: "<div><slot /></div>" },
  ContextMenuContent: { template: "<div><slot /></div>" },
  ContextMenuItem: {
    props: ["disabled", "variant"],
    emits: ["select"],
    template: "<button type=\"button\" :disabled=\"disabled\" @click=\"$emit('select', $event)\"><slot /></button>",
  },
  ContextMenuSeparator: { template: "<hr>" },
  ContextMenuSub: { template: "<div><slot /></div>" },
  ContextMenuSubContent: { template: "<div><slot /></div>" },
  ContextMenuSubTrigger: { template: "<button type=\"button\"><slot /></button>" },
  ContextMenuTrigger: { template: "<div><slot /></div>" },
  ConfirmDeleteSessionDialog: { template: "<div />" },
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

function item(harnessType: string, overrides: Partial<SessionListItem> = {}) {
  return mount(SessionItem, { props: { active: false, session: session(harnessType, overrides) }, global: { stubs } });
}

describe("SessionItem: Save as workflow…", () => {
  beforeEach(() => {
    navigate.mockReset();
    draftFromSession.mockReset();
    workflowsOn.value = true;
  });

  it("is under Repeat on a schedule… and says what it costs, for a harness that can ask off the record", () => {
    const wrapper = item("opencode");

    const entry = wrapper.get("[data-testid='session-save-as-workflow']");
    expect(entry.text()).toContain("Save as workflow…");
    expect(wrapper.get("[data-testid='session-save-as-workflow-cost']").text()).toBe("Asks the model once, from the cache");
    expect(entry.attributes("title")).toBe("Asks the model once. From a session it reads the conversation from the cache, so it's cheap.");
    const labels = wrapper.findAll("button").map((b) => b.text());
    expect(labels.findIndex((l) => l.includes("Save as workflow…"))).toBe(labels.findIndex((l) => l.includes("Repeat on a schedule…")) + 1);
  });

  it("opens Workflows and asks for a draft from the session", async () => {
    const wrapper = item("opencode");

    await wrapper.get("[data-testid='session-save-as-workflow']").trigger("click");

    expect(navigate).toHaveBeenCalledWith({ to: "/workflows" });
    expect(draftFromSession).toHaveBeenCalledWith("session-1", "Fix the login bug");
  });

  it("isn't offered on a harness that can't ask off the record", () => {
    expect(item("claude-code").find("[data-testid='session-save-as-workflow']").exists()).toBe(false);
  });

  it("isn't offered with workflows off, or on an archived session", () => {
    expect(item("opencode", { retentionStatus: "archived" }).find("[data-testid='session-save-as-workflow']").exists()).toBe(false);
    workflowsOn.value = false;
    expect(item("opencode").find("[data-testid='session-save-as-workflow']").exists()).toBe(false);
  });
});
