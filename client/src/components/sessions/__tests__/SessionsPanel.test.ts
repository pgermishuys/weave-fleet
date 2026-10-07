import { flushPromises, mount } from "@vue/test-utils";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { ref, shallowRef } from "vue";
import SessionsPanel from "@/components/sessions/SessionsPanel.vue";
import type { ProjectResponse, SessionListItem } from "@/api/client";
import { HOME_MACHINE_KEY, saveMachines, setActiveMachine, type MachineConnection } from "@/lib/machines";
import { saveSessionListScroll } from "@/lib/session-list-scroll";
import { useMachinesStore } from "@/stores/machines";
import { useSessionsStore } from "@/stores/sessions";

vi.mock("@tanstack/vue-router", () => ({
  useRouter: () => ({ navigate: vi.fn() }),
  useLocation: () => ref("/"),
}));

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

  it("keeps that order when another machine is live: only the Live tag moves", async () => {
    setActiveMachine(aurora);
    useSessionsStore().setSessions([item("aurora-1", "Paint the fence", "p-fleet")]);
    const view = mountPanel();
    await flushPromises();

    expect(headers(view)).toEqual(["This machine", "aurora (live)", "hangar"]);
    // Home, not live, shows its tree from its last list; aurora's live tree sits in aurora's slot.
    const groups = view.findAll("[data-testid='machine-group']").map((group) => group.attributes("data-machine"));
    expect(groups).toEqual([HOME_MACHINE_KEY, hangar.id]);
  });

  it("keeps the list's place across the switch to another machine", async () => {
    const machines = useMachinesStore();
    machines.others = {
      ...machines.others,
      [aurora.id]: { sessions: [item("aurora-1", "Paint the fence", "p-lighthouse")], projects: [project("p-lighthouse", "Lighthouse", 1)], error: null, loadedAt: 1, loading: false },
    };
    const openOn = vi.spyOn(machines, "openOn").mockImplementation(() => {});
    useSessionsStore().setSessions([item("home-1", "Local work", "p-fleet")]);
    const view = mountPanel();
    await flushPromises();

    const list = view.get(".sessions-list").element as HTMLElement;
    list.scrollTop = 420;
    await view.get("[data-machine='aurora-id'] [data-testid='machine-session-row']").trigger("click");

    expect(openOn).toHaveBeenCalledWith(aurora.id, "/sessions/aurora-1?instanceId=inst-aurora-1", expect.objectContaining({
      sessions: [expect.objectContaining({ session: expect.objectContaining({ id: "home-1" }) })],
    }));
    view.unmount();

    // After the reload the list comes back to the same place once the lists are in.
    const reloaded = mountPanel();
    await flushPromises();
    expect((reloaded.get(".sessions-list").element as HTMLElement).scrollTop).toBe(420);
  });

  it("doesn't move a list the user already scrolled after the reload", async () => {
    saveSessionListScroll(420);
    useSessionsStore().setSessions([]);
    const view = mountPanel();
    const list = view.get(".sessions-list").element as HTMLElement;
    list.scrollTop = 30;

    useSessionsStore().setSessions([item("home-1", "Local work", "p-fleet")]);
    await flushPromises();

    expect(list.scrollTop).toBe(30);
  });
});
