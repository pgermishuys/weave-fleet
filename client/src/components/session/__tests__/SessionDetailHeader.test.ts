import { flushPromises, mount } from "@vue/test-utils";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { ref, type DefineComponent } from "vue";
import SessionDetailHeaderComponent from "@/components/session/SessionDetailHeader.vue";
import { useSidebarStore } from "@/stores/sidebar";
import { useSessionsStore } from "@/stores/sessions";
import type { SessionListItem } from "@/api/client";
import { MACHINE_TARGET, targetFor } from "@/lib/machine-target";
import { LIVE_MACHINES_PREFERENCE_KEY, saveMachines, type MachineConnection } from "@/lib/machines";
import { useMachinesStore } from "@/stores/machines";
import { usePreferencesStore } from "@/stores/preferences";

interface HeaderProps {
  id: string;
  title?: string;
  activityStatus?: string | null;
  lifecycleStatus?: string | null;
  retryAttempt?: number | null;
  retentionStatus?: string | null;
  editingTitle?: boolean;
  canRestore?: boolean;
  lineageParentId?: string | null;
  lineageKind?: "fork" | "started" | "subagent" | null;
}

// The named "actions" slot trips the mount() typings in @vue/test-utils 2.2.7.
const SessionDetailHeader = SessionDetailHeaderComponent as unknown as DefineComponent<HeaderProps>;

const { navigate, apiFetch } = vi.hoisted(() => ({ navigate: vi.fn(), apiFetch: vi.fn() }));
vi.mock("@tanstack/vue-router", () => ({ useRouter: () => ({ navigate }) }));
vi.mock("@/lib/api-client", () => ({ apiFetchOn: (_machine: unknown, ...args: unknown[]) => apiFetch(...args) }));

vi.mock("@/composables/use-harnesses", () => ({
  useHarnesses: () => ({ harnesses: ref([]) }),
}));

function mountHeader(props: Partial<HeaderProps> = {}) {
  return mount(SessionDetailHeader, {
    props: {
      id: "session-1",
      title: "Consolidate status indicators",
      activityStatus: "busy",
      lifecycleStatus: "running",
      ...props,
    },
    global: {
      stubs: {
        SessionContextChips: true,
        SessionAnalyticsPopover: true,
      },
    },
  });
}

describe("SessionDetailHeader status", () => {
  it("shows no visible status while a sessions list is on screen", () => {
    useSidebarStore().registerSessionList();
    const wrapper = mountHeader();

    expect(wrapper.find("[data-testid='session-header-glyph']").exists()).toBe(false);
    expect(wrapper.find("[data-testid='session-retry-note']").exists()).toBe(false);
  });

  it("keeps a screen-reader status with the activity for tests and assistive tech", () => {
    const wrapper = mountHeader({ activityStatus: "idle" });
    const status = wrapper.get("[data-testid='session-status-indicator']");

    expect(status.attributes("role")).toBe("status");
    expect(status.classes()).toContain("sr-only");
    expect(status.attributes("data-status")).toBe("idle");
    expect(status.text()).toBe("Idle");
  });

  it("shows the row's glyph before the title when no sessions list is on screen", async () => {
    const release = useSidebarStore().registerSessionList();
    const wrapper = mountHeader();
    expect(wrapper.find("[data-testid='session-header-glyph']").exists()).toBe(false);

    release();
    await wrapper.vm.$nextTick();

    const glyph = wrapper.get("[data-testid='session-header-glyph']");
    expect(glyph.classes()).toContain("status-glyph--working");
    expect(glyph.attributes("aria-label")).toBe("Working");
  });

  it("names a retry and its attempt next to the title", () => {
    useSidebarStore().registerSessionList();
    const wrapper = mountHeader({ activityStatus: "retry", retryAttempt: 3 });

    expect(wrapper.get("[data-testid='session-retry-note']").text()).toBe("Retrying · attempt 3");
    expect(wrapper.get("[data-testid='session-status-indicator']").attributes("data-status")).toBe("retry");
  });

  it("says a session stopped on a question needs input, with the row's diamond", () => {
    const wrapper = mountHeader({ activityStatus: "waiting_input" });
    const status = wrapper.get("[data-testid='session-status-indicator']");

    expect(status.attributes("data-status")).toBe("waiting");
    expect(status.text()).toBe("Needs input");
    expect(wrapper.get("[data-testid='session-header-glyph']").attributes("aria-label")).toBe("Needs input");
  });
});

describe("SessionDetailHeader rename and restore", () => {
  it("asks to edit the title on double-click", async () => {
    const wrapper = mountHeader();

    await wrapper.get("[data-testid='session-title']").trigger("dblclick");

    expect(wrapper.emitted("update:editingTitle")).toEqual([[true]]);
  });

  it("does not offer rename for an archived session", async () => {
    const wrapper = mountHeader({ retentionStatus: "archived" });

    await wrapper.get("[data-testid='session-title']").trigger("dblclick");

    expect(wrapper.emitted("update:editingTitle")).toBeUndefined();
  });

  it("saves a changed title on Enter", async () => {
    const wrapper = mountHeader({ editingTitle: true });
    const input = wrapper.get<HTMLInputElement>("[data-testid='session-title-input']");

    await input.setValue("Unify status");
    await input.trigger("keydown", { key: "Enter" });

    expect(wrapper.emitted("rename")).toEqual([["Unify status"]]);
    expect(wrapper.emitted("update:editingTitle")).toEqual([[false]]);
  });

  it("keeps the old title on Escape", async () => {
    const wrapper = mountHeader({ editingTitle: true });
    const input = wrapper.get<HTMLInputElement>("[data-testid='session-title-input']");

    await input.setValue("Something else");
    await input.trigger("keydown", { key: "Escape" });

    expect(wrapper.emitted("rename")).toBeUndefined();
    expect(wrapper.emitted("update:editingTitle")).toEqual([[false]]);
  });

  it("offers Restore on the archived banner", async () => {
    const wrapper = mountHeader({ retentionStatus: "archived", canRestore: true });

    await wrapper.get("[data-testid='session-restore-button']").trigger("click");

    expect(wrapper.emitted("restore")).toHaveLength(1);
  });
});

describe("SessionDetailHeader lineage", () => {
  beforeEach(() => {
    navigate.mockReset();
    apiFetch.mockReset();
  });

  it("links a session another session started back to it, by its title", async () => {
    useSessionsStore().setSessions([{
      harnessType: "opencode",
      instanceId: "i-parent", workspaceId: "w", workspaceDirectory: "/repo", workspaceDisplayName: null, isolationStrategy: "existing",
      sessionStatus: "idle", session: { id: "parent", title: "What can we learn from t3code?" } as SessionListItem["session"],
      instanceStatus: "running", lifecycleStatus: "running", retentionStatus: "active", typedInstanceStatus: "running",
      isHidden: false, tags: [],
    }]);
    const wrapper = mountHeader({ lineageParentId: "parent", lineageKind: "started" });
    const link = wrapper.get("[data-testid='session-lineage-link']");

    expect(link.text()).toBe("Started by What can we learn from t3code?");
    // On a phone the pill shows only its icon; its label still names the parent.
    expect(link.attributes("aria-label")).toBe("This session was started by What can we learn from t3code?");
    expect(link.attributes("href")).toBe("/sessions/parent?instanceId=i-parent");
    await link.trigger("click", { button: 0 });
    expect(navigate).toHaveBeenCalledWith({
      to: "/sessions/$id", params: { id: "parent" }, search: { instanceId: "i-parent", parentSessionId: undefined },
    });
    expect(apiFetch).not.toHaveBeenCalled();
  });

  it("says Forked from, fetching the title of a parent the list doesn't hold", async () => {
    apiFetch.mockResolvedValue({ ok: true, json: async () => ({ title: "An archived session" }) });
    const wrapper = mountHeader({ lineageParentId: "archived-parent", lineageKind: "fork" });
    await flushPromises();

    expect(apiFetch).toHaveBeenCalledWith("/api/sessions/archived-parent");
    expect(wrapper.get("[data-testid='session-lineage-link']").text()).toBe("Forked from An archived session");
  });

  it("has no link for a session the user started", () => {
    expect(mountHeader().find("[data-testid='session-lineage-link']").exists()).toBe(false);
  });
});

describe("SessionDetailHeader for a session on a machine that isn't answering", () => {
  const mini: MachineConnection = {
    id: "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
    name: "mini",
    baseUrl: "http://mini.example.test:2113",
    token: "mini-token-0123456789",
    addedAt: "2026-10-08T00:00:00.000Z",
  };

  function mountOnMini(on: boolean) {
    saveMachines([mini]);
    const preferences = usePreferencesStore();
    preferences.hasFetched = true;
    preferences.preferences = { [LIVE_MACHINES_PREFERENCE_KEY]: on ? "true" : "false" };
    useMachinesStore().others = { [mini.id]: { sessions: [], projects: [], error: "Can't reach mini.", loadedAt: 1, loading: false } };
    return mount(SessionDetailHeader, {
      props: { id: "session-1", title: "Hero image sizes", activityStatus: "busy", lifecycleStatus: "running" },
      global: {
        stubs: { SessionContextChips: true, SessionAnalyticsPopover: true },
        provide: { [MACHINE_TARGET]: () => targetFor(mini) },
      },
    });
  }

  it("says the machine isn't answering, instead of Working", () => {
    const wrapper = mountOnMini(true);

    expect(wrapper.get("[data-testid='session-status-indicator']").attributes("data-status")).toBe("not-answering");
    expect(wrapper.get("[data-testid='session-status-indicator']").text()).toBe("Not answering");
    expect(wrapper.get("[data-testid='session-machine']").text()).toBe("mini · not answering");
  });

  it("stays Working with the switch off", () => {
    const wrapper = mountOnMini(false);

    expect(wrapper.get("[data-testid='session-status-indicator']").attributes("data-status")).toBe("working");
    expect(wrapper.get("[data-testid='session-machine']").text()).toBe("mini");
  });
});
