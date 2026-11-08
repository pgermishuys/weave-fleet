import { enableAutoUnmount, flushPromises, mount } from "@vue/test-utils";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { ref, shallowRef, type Ref } from "vue";
import MachineSessionsGroup from "@/components/sessions/MachineSessionsGroup.vue";
import SessionsPanel from "@/components/sessions/SessionsPanel.vue";
import type { ProjectResponse, SessionListItem } from "@/api/client";
import { HOME_MACHINE_KEY, saveMachines, setActiveMachine, type MachineConnection } from "@/lib/machines";
import { useSettingsNav } from "@/composables/use-settings-nav";
import { useMachinesStore } from "@/stores/machines";
import { useSessionsStore } from "@/stores/sessions";
import { useWorkspaceUiStore } from "@/stores/workspace-ui";

const { navigate, route } = vi.hoisted(() => ({ navigate: vi.fn(), route: { path: undefined as unknown as Ref<string> } }));

vi.mock("@tanstack/vue-router", async () => {
  const vue = await import("vue");
  route.path = vue.ref("/");
  return { useRouter: () => ({ navigate }), useLocation: () => route.path };
});

const { liveProjects } = vi.hoisted(() => ({ liveProjects: { value: [] as ProjectResponse[] } }));

vi.mock("@/composables/use-sessions", () => ({
  useSessions: () => ({ isLoading: shallowRef(false), error: shallowRef(undefined), refetch: vi.fn() }),
}));
vi.mock("@/composables/use-projects", () => ({
  useProjects: () => ({ projects: ref(liveProjects.value), isLoading: shallowRef(false), error: shallowRef(undefined), refetch: vi.fn() }),
}));
vi.mock("@/composables/use-running-work", () => ({
  useRunningWorkAcrossSessions: () => ({ running: ref([]), groups: ref([]) }),
}));

const aurora: MachineConnection = {
  id: "aurora-id",
  name: "aurora",
  baseUrl: "http://aurora.test:2113",
  token: "aurora-token-0123456789",
  addedAt: "2026-10-01T00:00:00.000Z",
};
const hangar: MachineConnection = { ...aurora, id: "hangar-id", name: "hangar", baseUrl: "http://hangar.test:2113" };

function project(id: string, name: string, position: number): ProjectResponse {
  return { id, name, type: "user", position, description: null, sessionCount: 0, createdAt: "", updatedAt: "" };
}

function item(id: string, title: string, projectId: string): SessionListItem {
  return {
    harnessType: "opencode",
    instanceId: `inst-${id}`, workspaceId: "w", workspaceDirectory: "/repo", workspaceDisplayName: null, isolationStrategy: "existing",
    sessionStatus: "idle", activityStatus: "idle", session: { id, title, time: { created: 1, updated: 1 }, tags: [] } as never,
    instanceStatus: "running", lifecycleStatus: "running", retentionStatus: "active", typedInstanceStatus: "running",
    isHidden: false, tags: [], projectId,
  };
}

function mountPanel() {
  return mount(SessionsPanel, {
    attachTo: document.body,
    global: { stubs: { NewProjectDialog: true } },
  });
}

function headers(view: ReturnType<typeof mountPanel>): string[] {
  return view.findAll("[data-testid='machine-header']").map((header) =>
    `${header.get(".machine-header__name").text()}${header.attributes("data-machine-live") ? " (live)" : ""}`);
}

// A panel left mounted keeps running, and its stores make its own Pinia the active one for the next test.
enableAutoUnmount(afterEach);

/** The rows marked open, as `machine/session`: `live/` for the live machine's tree. */
function openRows(view: ReturnType<typeof mountPanel>): string[] {
  return view.findAll("[aria-current='true']").map((row) => {
    const machine = row.element.closest("[data-testid='machine-group']")?.getAttribute("data-machine") ?? "live";
    return `${machine}/${row.element.closest("[data-session-id]")?.getAttribute("data-session-id")}`;
  });
}

/** jsdom has no layout: scrollTop keeps whatever it's set to. */
const scrollTops = new WeakMap<Element, number>();

describe("SessionsPanel across machines", () => {
  beforeEach(() => {
    localStorage.clear();
    sessionStorage.clear();
    setActiveMachine(null);
    saveMachines([aurora, hangar]);
    vi.stubGlobal("fetch", vi.fn(async () => new Response("{}", { status: 404 })));
    Object.defineProperty(HTMLElement.prototype, "scrollTop", {
      configurable: true,
      get(this: Element) {
        return scrollTops.get(this) ?? 0;
      },
      set(this: Element, value: number) {
        scrollTops.set(this, value);
      },
    });
    liveProjects.value = [project("p-fleet", "weave-fleet", 1)];
    route.path.value = "/";
  });

  afterEach(() => {
    setActiveMachine(null);
    vi.unstubAllGlobals();
    document.body.innerHTML = "";
  });

  it("lists home first and the other machines in the order they were added, with the live one tagged", async () => {
    useSessionsStore().setSessions([item("home-1", "Local work", "p-fleet")]);
    const view = mountPanel();
    await flushPromises();

    expect(headers(view)).toEqual(["This machine (live)", "aurora", "hangar"]);
  });

  it("ends the machine groups with an Add a machine row that opens Settings → Machines with the form asked for", async () => {
    const view = mountPanel();
    await flushPromises();

    await view.get("[data-testid='sessions-add-machine']").trigger("click");

    expect(useSettingsNav().activeSection.value).toBe("machines");
    expect(useSettingsNav().addMachineRequested.value).toBe(true);
    useSettingsNav().addMachineRequested.value = false;
  });

  it("keeps that order when another machine is live: only the live tree moves", async () => {
    setActiveMachine(aurora);
    useSessionsStore().setSessions([item("aurora-1", "Paint the fence", "p-fleet")]);
    const view = mountPanel();
    await flushPromises();

    expect(headers(view)).toEqual(["This machine", "aurora (live)", "hangar"]);
    // Home, not live, shows its tree from its last list; aurora's live tree sits in aurora's slot.
    const groups = view.findAll("[data-testid='machine-group']").map((group) => group.attributes("data-machine"));
    expect(groups).toEqual([HOME_MACHINE_KEY, hangar.id]);
  });

  it("opens another machine's session in place, without a reload", async () => {
    navigate.mockClear();
    const machines = useMachinesStore();
    machines.others = {
      ...machines.others,
      [hangar.id]: { sessions: [item("hangar-1", "Index for slow search", "p-lumen")], projects: [project("p-lumen", "lumen-site", 1)], error: null, loadedAt: 1, loading: false },
    };
    useSessionsStore().setSessions([item("home-1", "Local work", "p-fleet")]);
    const view = mountPanel();
    await flushPromises();

    await view.get("[data-machine='hangar-id'] [data-testid='machine-session-row']").trigger("click");

    expect(navigate).toHaveBeenCalledWith({
      to: "/sessions/$id",
      params: { id: "hangar-1" },
      search: { instanceId: "inst-hangar-1", parentSessionId: undefined },
    });
    expect(machines.sessionTarget("hangar-1")).toMatchObject({ key: hangar.id, isLive: false });
    view.unmount();
  });

  it("starts a new session on another machine from its heading, without switching to it", async () => {
    useSessionsStore().setSessions([item("home-1", "Local work", "p-fleet")]);
    const view = mountPanel();
    await flushPromises();

    const group = view.findAllComponents(MachineSessionsGroup).find((candidate) => candidate.props("machine").key === hangar.id)!;
    group.vm.$emit("newSession");

    expect(useWorkspaceUiStore().newSessionMachine).toBe(hangar.id);
    view.unmount();
  });

  // Regression (0.69.0): once machines switched in place, nothing made another machine live, and its projects lost
  // their + and menu for good.
  it("gives another machine's projects their + and menu, and the + starts a session there, in that project", async () => {
    navigate.mockClear();
    const machines = useMachinesStore();
    machines.others = {
      ...machines.others,
      [hangar.id]: { sessions: [item("hangar-1", "Index for slow search", "p-lumen")], projects: [project("p-lumen", "lumen-site", 1)], error: null, loadedAt: 1, loading: false },
    };
    useSessionsStore().setSessions([item("home-1", "Local work", "p-fleet")]);
    const view = mountPanel();
    await flushPromises();

    // Open hangar's session in place: hangar is the machine you're on, home stays live.
    await view.get("[data-machine='hangar-id'] [data-testid='machine-session-row']").trigger("click");
    machines.setCurrent(hangar.id);
    navigate.mockClear();

    // The right-click menu is on the project, as on home's (its trigger carries the menu's state).
    expect(view.get("[data-machine='hangar-id'] .project-shell").attributes("data-state")).toBe("closed");

    await view.get("[data-machine='hangar-id'] [data-testid='project-new-session']").trigger("click");

    expect(useWorkspaceUiStore().newSessionMachine).toBeNull(); // null is the machine you're on: hangar
    expect(machines.currentKey).toBe(hangar.id);
    expect(navigate).toHaveBeenCalledWith({ to: "/sessions/new", search: { projectId: "p-lumen", source: undefined } });
    view.unmount();
  });

  it("starts a session in another machine's project from its +, on that machine, while you're on home", async () => {
    navigate.mockClear();
    const machines = useMachinesStore();
    machines.others = {
      ...machines.others,
      [hangar.id]: { sessions: [], projects: [project("p-lumen", "lumen-site", 1)], error: null, loadedAt: 1, loading: false },
    };
    const view = mountPanel();
    await flushPromises();

    await view.get("[data-machine='hangar-id'] [data-testid='project-new-session']").trigger("click");

    expect(useWorkspaceUiStore().newSessionMachine).toBe(hangar.id);
    expect(navigate).toHaveBeenCalledWith({ to: "/sessions/new", search: { projectId: "p-lumen", source: undefined } });
    view.unmount();
  });

  it("renames another machine's project on that machine, and lists that machine again", async () => {
    const fetchMock = vi.fn(async (input: RequestInfo | URL) => {
      const url = input instanceof Request ? input.url : String(input);
      return url.includes("/api/projects/p-lumen")
        ? new Response(JSON.stringify(project("p-lumen", "lumen-docs", 1)), { status: 200, headers: { "Content-Type": "application/json" } })
        : new Response("{}", { status: 404 });
    });
    vi.stubGlobal("fetch", fetchMock);
    const machines = useMachinesStore();
    machines.others = {
      ...machines.others,
      [hangar.id]: { sessions: [], projects: [project("p-lumen", "lumen-site", 1)], error: null, loadedAt: 1, loading: false },
    };
    const refresh = vi.spyOn(machines, "refreshMachine").mockResolvedValue();
    const view = mountPanel();
    await flushPromises();

    await view.get("[data-machine='hangar-id'] .project-header").trigger("keydown", { key: "F2" });
    const input = view.get("[data-machine='hangar-id'] input");
    await input.setValue("lumen-docs");
    await input.trigger("keydown", { key: "Enter" });
    await flushPromises();

    const renames = fetchMock.mock.calls
      .map(([input]) => (input instanceof Request ? input : null))
      .filter((request): request is Request => request !== null && request.url.includes("/api/projects/p-lumen"));
    expect(renames.map((request) => `${request.method} ${request.url}`)).toEqual(["PATCH http://hangar.test:2113/api/projects/p-lumen"]);
    expect(refresh).toHaveBeenCalledWith(hangar.id);
    view.unmount();
  });

  it("Work here makes that machine the one you're on and goes to its first page in place", async () => {
    navigate.mockClear();
    const machines = useMachinesStore();
    const view = mountPanel();
    await flushPromises();

    const group = view.findAllComponents(MachineSessionsGroup).find((candidate) => candidate.props("machine").key === hangar.id)!;
    group.vm.$emit("workHere");

    expect(machines.currentKey).toBe(hangar.id);
    expect(navigate).toHaveBeenCalledWith({ to: "/" });
    view.unmount();
  });

  it("marks the open session's row on the machine it's on, and only that row, with no Live badge on any heading", async () => {
    const machines = useMachinesStore();
    machines.others = {
      ...machines.others,
      [hangar.id]: { sessions: [item("hangar-1", "Index for slow search", "p-lumen")], projects: [project("p-lumen", "lumen-site", 1)], error: null, loadedAt: 1, loading: false },
    };
    useSessionsStore().setSessions([item("home-1", "Local work", "p-fleet"), item("home-2", "Tidy the docs", "p-fleet")]);
    route.path.value = "/sessions/home-1";
    const view = mountPanel();
    await flushPromises();
    expect(openRows(view)).toEqual(["live/home-1"]);

    // Open hangar's session in place: its row is marked, and home's isn't left marked.
    await view.get("[data-machine='hangar-id'] [data-testid='machine-session-row']").trigger("click");
    route.path.value = "/sessions/hangar-1";
    await flushPromises();
    expect(openRows(view)).toEqual(["hangar-id/hangar-1"]);
    expect(view.find(".machine-header__live").exists()).toBe(false);
    expect(view.findAll("[data-testid='machine-header']").every((header) => !header.text().includes("Live"))).toBe(true);

    // And back to one of home's.
    route.path.value = "/sessions/home-2";
    await flushPromises();
    expect(openRows(view)).toEqual(["live/home-2"]);
    view.unmount();
  });

  it("marks another machine's session open after a reload with it open", async () => {
    useMachinesStore().rememberSessions(hangar.id, ["hangar-1"]);
    const machines = useMachinesStore();
    machines.others = {
      ...machines.others,
      [hangar.id]: { sessions: [item("hangar-1", "Index for slow search", "p-lumen")], projects: [project("p-lumen", "lumen-site", 1)], error: null, loadedAt: 1, loading: false },
    };
    useSessionsStore().setSessions([item("home-1", "Local work", "p-fleet")]);
    route.path.value = "/sessions/hangar-1";
    const view = mountPanel();
    await flushPromises();

    expect(openRows(view)).toEqual(["hangar-id/hangar-1"]);
    view.unmount();
  });
});

describe("SessionsPanel with no other machines", () => {
  beforeEach(() => {
    localStorage.clear();
    sessionStorage.clear();
    setActiveMachine(null);
    saveMachines([]);
    vi.stubGlobal("fetch", vi.fn(async () => new Response("{}", { status: 404 })));
    liveProjects.value = [project("p-fleet", "weave-fleet", 1)];
    route.path.value = "/";
  });

  afterEach(() => {
    vi.unstubAllGlobals();
    document.body.innerHTML = "";
  });

  it("shows no machine headings and marks the open session's row, as it always has", async () => {
    useSessionsStore().setSessions([item("home-1", "Local work", "p-fleet"), item("home-2", "Tidy the docs", "p-fleet")]);
    route.path.value = "/sessions/home-2";
    const view = mountPanel();
    await flushPromises();

    expect(view.findAll("[data-testid='machine-header']")).toHaveLength(0);
    expect(view.findAll("[data-testid='machine-group']")).toHaveLength(0);
    expect(openRows(view)).toEqual(["live/home-2"]);

    route.path.value = "/sessions/home-1";
    await flushPromises();
    expect(openRows(view)).toEqual(["live/home-1"]);
    view.unmount();
  });
});
