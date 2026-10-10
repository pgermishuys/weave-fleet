import { enableAutoUnmount, flushPromises, mount } from "@vue/test-utils";
import { createPinia, setActivePinia } from "pinia";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { defineComponent } from "vue";
import type { ModsRuntimeJob, ModsRuntimeView } from "@/lib/mods-runtime";

const { handlers, navigate, setActiveSection } = vi.hoisted(() => ({
  handlers: [] as ((event: { type: string; payload: unknown }) => void)[],
  navigate: vi.fn(),
  setActiveSection: vi.fn(),
}));

vi.mock("@/composables/use-signalr-socket", () => ({
  onGlobalEvent: (_machine: unknown, topic: string, handler: (event: { type: string; payload: unknown }) => void) => {
    if (topic === "sessions") handlers.push(handler);
    return () => handlers.splice(handlers.indexOf(handler), 1);
  },
}));
vi.mock("@tanstack/vue-router", () => ({ useRouter: () => ({ navigate }) }));
vi.mock("@/composables/use-settings-nav", () => ({ useSettingsNav: () => ({ setActiveSection }) }));
vi.mock("@/api/client", () => ({ api: { GET: vi.fn().mockResolvedValue({ data: {} }), PUT: vi.fn().mockResolvedValue({}) } }));

const { useModsRuntimeNotices } = await import("@/composables/use-mods-runtime-notices");
const { useModsRuntimeStore } = await import("@/stores/mods-runtime");
const { useNoticesStore } = await import("@/stores/notices");

enableAutoUnmount(afterEach);

const Host = defineComponent({
  setup() {
    useModsRuntimeNotices();
    return () => null;
  },
});

let answer: ModsRuntimeView;
let posts: string[];

function view(overrides: Partial<ModsRuntimeView> = {}): ModsRuntimeView {
  return {
    bun: { path: "/b", displayPath: "~/b", source: "installed", version: "1.4.2", safe: true },
    release: { version: "1.4.2", hasBuild: true, size: 35_000_000, installFolder: "~/.weave/runtimes/bun/1.4.2", source: "github.com/oven-sh/bun" },
    installedSize: 1,
    job: null,
    ...overrides,
  };
}

function job(overrides: Partial<ModsRuntimeJob> = {}): ModsRuntimeJob {
  return { phase: "downloading", kind: "install", version: "1.4.2", bytesReceived: 12_000_000, bytesTotal: 35_000_000, ...overrides };
}

const failedSecurity = job({
  kind: "security",
  version: "1.4.3",
  from: "1.4.2",
  phase: "failed",
  reason: "offline",
  message: "Fleet couldn't reach github.com: the connection timed out. Check that this computer is online, then try again.",
});

function emit(type: string, payload: unknown): void {
  for (const handler of [...handlers]) handler({ type, payload });
}

async function start(initial: ModsRuntimeView) {
  answer = initial;
  const wrapper = mount(Host);
  await flushPromises();
  return { wrapper, notices: useNoticesStore(), store: useModsRuntimeStore() };
}

describe("mods runtime notices", () => {
  beforeEach(() => {
    localStorage.clear();
    setActivePinia(createPinia());
    handlers.length = 0;
    navigate.mockReset();
    setActiveSection.mockReset();
    posts = [];
    vi.stubGlobal(
      "fetch",
      vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
        const method = (init?.method ?? "GET").toUpperCase();
        if (method === "POST") posts.push(new URL(String(input), "http://localhost").pathname);
        return new Response(JSON.stringify(answer));
      }),
    );
  });

  afterEach(() => vi.unstubAllGlobals());

  it("posts nothing when Bun is fine and no job is running", async () => {
    const { notices } = await start(view());

    expect(notices.notices).toHaveLength(0);
  });

  it("posts the security notice, in warn tone, when a security install fails", async () => {
    const { notices } = await start(view({ job: failedSecurity, update: { version: "1.4.3", security: true } }));

    const notice = notices.notices.find((item) => item.id === "mods-runtime-security-failed:1.4.3");
    expect(notice?.title).toBe("Bun needs a security fix");
    expect(notice?.tone).toBe("warn");
    expect(notice?.body).toBe("Mods run on Bun 1.4.2, which has a security problem fixed in 1.4.3. Fleet couldn't download it: the connection timed out.");
    expect(notice?.actions?.map((action) => [action.label, action.tone])).toEqual([
      ["Retry", "primary"],
      ["Open Settings", undefined],
    ]);
  });

  it("posts the security notice when the failure arrives as an event, and removes it when the install succeeds", async () => {
    const { notices } = await start(view());
    answer = view({ job: failedSecurity });

    emit("mods.runtime", { job: failedSecurity, reason: "job" });
    await flushPromises();
    expect(notices.has("mods-runtime-security-failed:1.4.3")).toBe(true);

    answer = view({ bun: { path: "/b", displayPath: "~/b", source: "installed", version: "1.4.3", safe: true }, job: job({ kind: "security", version: "1.4.3", phase: "succeeded" }) });
    emit("mods.runtime", { job: answer.job, reason: "installed" });
    await flushPromises();
    expect(notices.has("mods-runtime-security-failed:1.4.3")).toBe(false);
  });

  it("doesn't post it for a failed first install or a failed ordinary update", async () => {
    const { notices } = await start(view({ job: job({ phase: "failed", reason: "offline", message: "x" }) }));
    expect(notices.notices).toHaveLength(0);

    answer = view({ job: job({ kind: "update", version: "1.4.3", phase: "failed", reason: "offline", message: "x" }) });
    emit("mods.runtime", { job: answer.job, reason: "job" });
    await flushPromises();
    expect(notices.notices).toHaveLength(0);
  });

  it("Retry posts the install; Open Settings goes to Settings → Features", async () => {
    const { notices } = await start(view({ job: failedSecurity }));
    const actions = notices.notices[0]!.actions!;

    await actions[0]!.run();
    expect(posts).toEqual(["/api/features/mods/runtime/install"]);

    await actions[1]!.run();
    expect(setActiveSection).toHaveBeenCalledWith("features");
    expect(navigate).toHaveBeenCalledWith({ to: "/settings" });
  });

  it("warns when your own Bun is unsafe, with the server's message, and drops it once it's safe", async () => {
    const unsafe = view({
      bun: { path: "/opt/bun", displayPath: "/opt/bun", source: "configured", version: "1.4.2", safe: false, message: "Bun 1.4.2 has a security problem. Update to 1.4.3." },
      configuredPath: "/opt/bun",
    });
    const { notices } = await start(unsafe);

    const notice = notices.notices.find((item) => item.id === "mods-runtime-own-unsafe:1.4.2");
    expect(notice?.body).toBe("Bun 1.4.2 has a security problem. Update to 1.4.3.");
    expect(notice?.tone).toBe("warn");
    expect(notice?.actions?.map((action) => action.label)).toEqual(["Open Settings"]);

    answer = view({ bun: { path: "/opt/bun", displayPath: "/opt/bun", source: "configured", version: "1.4.3", safe: true }, configuredPath: "/opt/bun" });
    emit("mods.runtime", { job: null, reason: "bun-path" });
    await flushPromises();
    expect(notices.notices).toHaveLength(0);
  });

  it("tells you a draft is waiting while Bun installs, and stops when the job ends", async () => {
    const { notices } = await start(view({ bun: null, job: job() }));

    emit("mods.changed", { name: "tool-counts", reason: "draft-on", sessionId: "ses_1" });
    await flushPromises();

    const notice = notices.notices.find((item) => item.id === "mods-runtime-draft-waiting");
    expect(notice?.title).toBe("Your mod starts when Bun is installed");
    expect(notice?.body).toBe("Fleet is still downloading Bun 1.4.2 (12 of 35 MB). The mod starts as soon as it's done.");
    expect(notice?.actions?.map((action) => action.label)).toEqual(["Open Settings"]);

    answer = view({ job: job({ phase: "succeeded" }) });
    emit("mods.runtime", { job: answer.job, reason: "installed" });
    await flushPromises();
    expect(notices.has("mods-runtime-draft-waiting")).toBe(false);
  });

  it("doesn't tell you when no job is running, or the change isn't a draft's", async () => {
    const { notices } = await start(view());
    emit("mods.changed", { name: "tool-counts", reason: "draft-on", sessionId: "ses_1" });
    await flushPromises();
    expect(notices.notices).toHaveLength(0);

    answer = view({ bun: null, job: job() });
    emit("mods.runtime", { job: job(), reason: "job" });
    await flushPromises();
    emit("mods.changed", { name: "tool-counts", reason: "kept" });
    await flushPromises();
    expect(notices.has("mods-runtime-draft-waiting")).toBe(false);
  });
});
