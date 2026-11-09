import { enableAutoUnmount, flushPromises, mount } from "@vue/test-utils";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import NewProjectDialog from "@/components/sessions/NewProjectDialog.vue";
import { saveMachines, setActiveMachine, type MachineConnection } from "@/lib/machines";

vi.mock("@tanstack/vue-router", () => ({ useNavigate: () => vi.fn(), useRouter: () => ({ navigate: vi.fn() }) }));

const kestrel: MachineConnection = {
  id: "kestrel-id",
  name: "kestrel",
  baseUrl: "http://kestrel.test:2113",
  token: "kestrel-token-0123456789",
  addedAt: "2026-10-01T00:00:00.000Z",
};

enableAutoUnmount(afterEach);

/** Answers POST /api/projects with a new project, and keeps the requests; every read gets a 404. */
function recordProjectPosts(failOn?: string): Request[] {
  const posts: Request[] = [];
  vi.stubGlobal("fetch", vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
    const request = input instanceof Request ? input : new Request(String(input), init);
    const url = new URL(request.url);
    if (request.method !== "POST" || url.pathname !== "/api/projects") return new Response("{}", { status: 404 });
    if (failOn && url.host === failOn) throw new TypeError("Failed to fetch");
    posts.push(request);
    const body = { id: "p-new", name: "Release pipeline", type: "user", position: 3, sessionCount: 0, createdAt: "", updatedAt: "" };
    return new Response(JSON.stringify(body), { status: 201, headers: { "Content-Type": "application/json" } });
  }));
  return posts;
}

function mountDialog(props: Record<string, unknown> = {}) {
  return mount(NewProjectDialog, {
    attachTo: document.body,
    props: { open: true, ...props },
    global: { stubs: { teleport: false } },
  });
}

function machineChip(): HTMLElement | null {
  return document.body.querySelector<HTMLElement>("[data-testid='new-session-machine']");
}

async function submit(name: string): Promise<void> {
  const input = document.body.querySelector<HTMLInputElement>("#new-project-name")!;
  input.value = name;
  input.dispatchEvent(new Event("input"));
  await flushPromises();
  document.body.querySelector<HTMLFormElement>("form")!.dispatchEvent(new Event("submit", { cancelable: true }));
  await flushPromises();
}

describe("NewProjectDialog", () => {
  beforeEach(() => {
    localStorage.clear();
    sessionStorage.clear();
    setActiveMachine(null);
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  it("doesn't ask for a machine when there is only one, and creates the project on it", async () => {
    const posts = recordProjectPosts();
    const view = mountDialog();
    await flushPromises();

    expect(machineChip()).toBeNull();
    await submit("Release pipeline");

    expect(posts).toHaveLength(1);
    expect(new URL(posts[0]!.url).host).toBe(new URL(window.location.href).host);
    expect(view.emitted("created")?.[0]?.[1]).toBe("home");
  });

  it("creates the project on the machine picked, from a heading's New project on …", async () => {
    saveMachines([kestrel]);
    const posts = recordProjectPosts();
    const view = mountDialog({ machineKey: kestrel.id, open: false });
    await view.setProps({ open: true });
    await flushPromises();

    expect(machineChip()?.textContent).toContain("kestrel");
    await submit("Release pipeline");

    expect(posts).toHaveLength(1);
    expect(new URL(posts[0]!.url).host).toBe("kestrel.test:2113");
    expect(await posts[0]!.json()).toMatchObject({ name: "Release pipeline" });
    expect(view.emitted("created")?.[0]?.[1]).toBe(kestrel.id);
  });

  it("starts on the machine you're on, and lets you pick another", async () => {
    saveMachines([kestrel]);
    const posts = recordProjectPosts();
    const view = mountDialog({ open: false });
    await view.setProps({ open: true });
    await flushPromises();

    expect(machineChip()?.textContent).not.toContain("kestrel");
    machineChip()!.click();
    await flushPromises();
    document.body.querySelector<HTMLElement>(`[data-testid='new-session-machine-${kestrel.id}']`)!.click();
    await flushPromises();
    expect(machineChip()?.textContent).toContain("kestrel");

    await submit("Release pipeline");
    expect(new URL(posts[0]!.url).host).toBe("kestrel.test:2113");
  });

  it("keeps the dialog open and names the machine when it can't be reached", async () => {
    saveMachines([kestrel]);
    recordProjectPosts("kestrel.test:2113");
    const view = mountDialog({ machineKey: kestrel.id, open: false });
    await view.setProps({ open: true });
    await flushPromises();

    await submit("Release pipeline");

    expect(document.body.querySelector("[role='alert']")?.textContent).toContain("Can't reach kestrel. The project wasn't created.");
    expect(view.emitted("created")).toBeUndefined();
    expect(document.body.querySelector<HTMLInputElement>("#new-project-name")?.value).toBe("Release pipeline");
  });
});
