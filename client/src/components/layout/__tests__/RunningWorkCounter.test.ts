import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { flushPromises, mount } from "@vue/test-utils";
import type { SessionListItem } from "@/api/client";
import type { DomainEvent } from "@/lib/domain-events";

const { apiGet, navigate, globalHandlers } = vi.hoisted(() => ({
  apiGet: vi.fn(),
  navigate: vi.fn(),
  globalHandlers: [] as Array<(event: DomainEvent) => void>,
}));

vi.mock("@/api/client", () => ({ api: { GET: apiGet, POST: vi.fn() } }));
vi.mock("@/composables/use-signalr-socket", () => ({
  onGlobalEvent: (_machine: unknown, _topic: string, handler: (event: DomainEvent) => void) => {
    globalHandlers.push(handler);
    return () => undefined;
  },
  onReconnect: () => () => undefined,
}));
vi.mock("@tanstack/vue-router", () => ({ useRouter: () => ({ navigate }) }));

import StatusBar from "@/components/layout/StatusBar.vue";
import { _resetRunningWorkForTesting } from "@/composables/use-running-work";
import { useSessionsStore } from "@/stores/sessions";

const T0 = Date.parse("2026-10-04T10:03:12Z");

function shell(sessionId: string, id: string, label: string, extra: Record<string, unknown> = {}) {
  return {
    id, sessionId, workId: `sh_${id}`, kind: "shell", title: "shell", label, status: "running", background: true,
    canStop: true, canReadOutput: true, startedAt: "2026-10-04T10:00:00Z", ...extra,
  };
}

const reviewer = {
  id: "w-agent", sessionId: "s1", workId: "call_sub", kind: "subagent", title: "code-reviewer", label: "Review the diff",
  status: "running", background: true, childSessionId: "child-1", canStop: true, startedAt: "2026-10-04T10:01:32Z",
};

function sessions(): void {
  const store = useSessionsStore();
  store.setSessions([
    { session: { id: "s1", title: "Capture Claude Code subagents" }, instanceId: "i1", workspaceDisplayName: "weave-fleet" },
    { session: { id: "s2", title: "Running-work strip in the composer" }, instanceId: "i2", workspaceDisplayName: "weave-fleet" },
  ] as unknown as SessionListItem[]);
  store.setActiveSessionId("s1");
}

function counter(wrapper: { find: (selector: string) => ReturnType<ReturnType<typeof mount>["find"]> }) {
  return wrapper.find("[data-testid='running-work-counter']");
}

describe("the status bar's running-work counter", () => {
  beforeEach(() => {
    vi.useFakeTimers({ toFake: ["Date", "setInterval", "clearInterval"] });
    vi.setSystemTime(T0);
    _resetRunningWorkForTesting();
    globalHandlers.length = 0;
    apiGet.mockReset();
    navigate.mockReset();
  });

  afterEach(() => {
    vi.useRealTimers();
    document.body.innerHTML = "";
  });

  it("is hidden when nothing runs", async () => {
    apiGet.mockResolvedValue({ data: [], response: { ok: true, status: 200 } });
    const wrapper = mount(StatusBar);
    await flushPromises();

    expect(apiGet).toHaveBeenCalledWith("/api/work/running");
    expect(counter(wrapper).exists()).toBe(false);
  });

  it("counts what runs and in how many sessions", async () => {
    sessions();
    apiGet.mockResolvedValue({
      data: [shell("s2", "w-dev", "bun run dev --port 5174"), shell("s1", "w-e2e", "bun run test:e2e"), reviewer],
      response: { ok: true, status: 200 },
    });
    const wrapper = mount(StatusBar);
    await flushPromises();

    expect(counter(wrapper).text()).toBe("3 running in 2 sessions");
  });

  it("follows work starting and ending in any session without asking Fleet again", async () => {
    apiGet.mockResolvedValue({ data: [], response: { ok: true, status: 200 } });
    const wrapper = mount(StatusBar);
    await flushPromises();

    for (const handler of globalHandlers) handler({ type: "work.started", payload: shell("s1", "w-e2e", "bun run test:e2e") as never });
    await flushPromises();
    expect(counter(wrapper).text()).toBe("1 running in 1 session");

    for (const handler of globalHandlers) {
      handler({ type: "work.ended", payload: shell("s1", "w-e2e", "bun run test:e2e", { status: "completed", endedAt: "2026-10-04T10:03:12Z", endedReason: "completed" }) as never });
    }
    await flushPromises();
    expect(counter(wrapper).exists()).toBe(false);
    expect(apiGet).toHaveBeenCalledTimes(1);
  });

  it("opens the work grouped by session, this session first", async () => {
    sessions();
    apiGet.mockResolvedValue({
      data: [shell("s2", "w-dev", "bun run dev --port 5174"), shell("s1", "w-e2e", "bun run test:e2e"), reviewer],
      response: { ok: true, status: 200 },
    });
    const wrapper = mount(StatusBar, { attachTo: document.body, global: { stubs: { teleport: false } } });
    await flushPromises();

    await counter(wrapper).trigger("click");
    await flushPromises();

    const groups = [...document.querySelectorAll("[data-testid='running-work-group']")];
    expect(groups.map((group) => group.querySelector(".running-popover__title")?.textContent)).toEqual([
      "Capture Claude Code subagents",
      "Running-work strip in the composer",
    ]);
    expect(groups[0]!.textContent).toContain("this session");
    expect(groups[1]!.textContent).toContain("weave-fleet");
    const firstRows = [...groups[0]!.querySelectorAll("[data-testid='background-work-row']")];
    expect(firstRows.map((row) => row.querySelector(".work-row__what")?.getAttribute("title"))).toEqual([
      "bun run test:e2e",
      "code-reviewer · Review the diff",
    ]);
    expect(firstRows[0]!.textContent).toContain("3m");
    expect(firstRows[1]!.querySelector("[data-testid='background-work-open']")).not.toBeNull();
    wrapper.unmount();
  });
});
