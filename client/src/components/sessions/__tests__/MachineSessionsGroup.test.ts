import { mount } from "@vue/test-utils";
import { beforeEach, describe, expect, it, vi } from "vitest";
import MachineSessionsGroup from "@/components/sessions/MachineSessionsGroup.vue";
import type { MachineEntry, MachineSessions } from "@/stores/machines";
import { projectGroupKey, useSidebarStore } from "@/stores/sidebar";
import type { ProjectSummary } from "@/lib/session-project-groups";

vi.mock("@tanstack/vue-router", () => ({ useRouter: () => ({ navigate: vi.fn() }) }));

const macbook: MachineEntry = {
  key: "m-mac",
  name: "macbook",
  os: "macos",
  baseUrl: "http://mac.test:2113",
  isHome: false,
  isLive: false,
  connection: null,
};

const projects: ProjectSummary[] = [
  { id: "p-scratch", name: "Scratch", type: "scratch", position: 9 },
  { id: "p-bff", name: "BFF", type: "user", position: 2 },
  { id: "p-is", name: "IdentityServer", type: "user", position: 1 },
  { id: "p-empty", name: "Products", type: "user", position: 3 },
];

const listed: MachineSessions = { sessions: [], projects, error: null, loadedAt: 1, loading: false };

function item(id: string, title: string, extra: Record<string, unknown> = {}) {
  return {
    instanceId: id, workspaceId: "w", workspaceDirectory: "/repo", workspaceDisplayName: null, isolationStrategy: "existing",
    sessionStatus: "idle", activityStatus: "idle", session: { id, title, time: { created: 1, updated: 1 }, tags: [] },
    instanceStatus: "running", lifecycleStatus: "running", retentionStatus: "active", typedInstanceStatus: "running",
    isHidden: false, tags: [], ...extra,
  } as unknown as MachineSessions["sessions"][number];
}

function mountGroup(props: Partial<InstanceType<typeof MachineSessionsGroup>["$props"]> = {}) {
  return mount(MachineSessionsGroup, { props: { machine: macbook, state: listed, query: "", ...props } });
}

function groupNames(view: ReturnType<typeof mountGroup>): string[] {
  return view.findAll(".project-title").map((title) => title.text());
}

function rowIds(view: ReturnType<typeof mountGroup>): string[] {
  return view.findAll("[data-testid='machine-session-row']").map((row) => row.element.closest("[data-session-id]")!.getAttribute("data-session-id")!);
}

describe("MachineSessionsGroup", () => {
  beforeEach(() => {
    localStorage.clear();
  });

  it("shows the new-session draft that starts on this machine, in its project, and opens it", async () => {
    const view = mountGroup({
      draft: { key: "new-session-draft-1", title: "Fix the sign-in loop", projectId: "p-bff", isStarting: false },
      draftActive: true,
    });

    const row = view.get("[data-testid='new-session-draft-row']");
    expect(row.text()).toContain("Fix the sign-in loop");
    expect(row.attributes("aria-current")).toBe("true");
    expect(row.element.closest("[data-project-id]")!.getAttribute("data-project-id")).toBe("p-bff");

    await row.trigger("click");
    expect(view.emitted("openDraft")).toHaveLength(1);
  });

  it("has no draft row for a draft on another machine, and says when it has no sessions", () => {
    const view = mountGroup({ state: { ...listed, projects: [] } });

    expect(view.find("[data-testid='new-session-draft-row']").exists()).toBe(false);
    expect(view.text()).toContain("No sessions");
  });

  it("groups its sessions like the live machine: Pinned first, then its projects in their order", () => {
    const state = {
      ...listed,
      sessions: [
        item("bff-1", "Warn on forwarded cookies", { projectId: "p-bff", projectName: "BFF" }),
        item("pin-1", "Release notes for 7.4", { projectId: "p-is", projectName: "IdentityServer", pinOrder: 1 }),
        item("is-1", "Count users across spaces", { projectId: "p-is", projectName: "IdentityServer" }),
        item("scratch-1", "Quick regex", { projectId: "p-scratch", projectName: "Scratch" }),
      ],
    };

    const view = mountGroup({ state });

    expect(groupNames(view)).toEqual(["Pinned", "IdentityServer", "BFF", "Products", "Scratch"]);
    expect(rowIds(view)).toEqual(["pin-1", "is-1", "bff-1", "scratch-1"]);
    expect(view.get("[data-testid='machine-header']").text()).toContain("4");
  });

  it("puts forks and started sessions under the top-level session they came from, and counts what runs", () => {
    const state = {
      ...listed,
      sessions: [
        item("fork", "Fork of the mapper", { projectId: "p-is", forkedFromSessionId: "parent", spawnKind: "fork", activityStatus: "busy", sessionStatus: "active" }),
        item("parent", "Capture subagents", { projectId: "p-is", runningWorkCount: 2 }),
        item("other", "Something else", { projectId: "p-is" }),
      ],
    };

    const view = mountGroup({ state });

    expect(rowIds(view)).toEqual(["parent", "fork", "other"]);
    expect(view.get("[data-session-id='parent'] [data-testid='session-running-chip']").text()).toContain("2");
    expect(view.get("[data-session-id='fork'] [data-testid='session-kind']").text()).toBe("fork");
  });

  it("filters by the search box, keeping a project whose name matches", () => {
    const state = {
      ...listed,
      sessions: [
        item("bff-1", "Warn on forwarded cookies", { projectId: "p-bff" }),
        item("is-1", "Count users", { projectId: "p-is" }),
      ],
    };

    expect(rowIds(mountGroup({ state, query: "cookies" }))).toEqual(["bff-1"]);
    expect(groupNames(mountGroup({ state, query: "identity" }))).toEqual(["IdentityServer"]);
    expect(mountGroup({ state, query: "nothing like it" }).text()).toContain("No matching sessions");
  });

  it("folds its projects under its own keys", async () => {
    const state = { ...listed, sessions: [item("is-1", "Count users", { projectId: "p-is" })] };
    const sidebar = useSidebarStore();
    sidebar.setGroupCollapsed(projectGroupKey(macbook.key, "p-is"), true);

    const view = mountGroup({ state });
    expect(rowIds(view)).toEqual([]);

    await view.get("[data-project-id='p-is'] .project-header").trigger("click");
    expect(sidebar.isGroupCollapsed(projectGroupKey(macbook.key, "p-is"))).toBe(false);
  });

  it("only opens a session: no pin, archive, drag, rename or project menu", async () => {
    const state = { ...listed, sessions: [item("is-1", "Count users", { projectId: "p-is" })] };
    const view = mountGroup({ state });

    const row = view.get("[data-testid='machine-session-row']");
    expect(row.attributes("title")).toBe("Open on macbook");
    expect(view.find("[data-testid='session-row-pin']").exists()).toBe(false);
    expect(view.find("[data-testid='session-row-archive']").exists()).toBe(false);
    expect(view.get("[data-session-id='is-1']").attributes("draggable")).toBe("false");

    await row.trigger("dblclick");
    expect(view.find("input[aria-label='Session name']").exists()).toBe(false);

    await row.trigger("click", { ctrlKey: true });
    const opened = view.emitted<[MachineSessions["sessions"][number]]>("open") ?? [];
    expect(opened.map(([session]) => session.session.id)).toEqual(["is-1"]);
  });
});
