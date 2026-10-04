import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { flushPromises, mount } from "@vue/test-utils";
import { computed, shallowRef, toValue, type MaybeRefOrGetter } from "vue";
import type { SessionListItem } from "@/api/client";
import type { AccumulatedMessage } from "@/lib/client-types";

const { apiGet, apiPost, apiFetch, navigate, childMessages } = vi.hoisted(() => ({
  apiGet: vi.fn(),
  apiPost: vi.fn(),
  apiFetch: vi.fn(),
  navigate: vi.fn(),
  childMessages: {} as Record<string, AccumulatedMessage[]>,
}));

vi.mock("@/api/client", () => ({ api: { GET: apiGet, POST: apiPost } }));
vi.mock("@/lib/api-client", () => ({ apiFetch }));
vi.mock("@/composables/use-signalr-socket", () => ({
  onGlobalEvent: () => () => undefined,
  onReconnect: () => () => undefined,
}));
vi.mock("@/composables/use-models", () => ({
  useModels: () => ({ models: shallowRef([{ id: "claude-sonnet-5-5", name: "Sonnet 5.5" }, { id: "gpt-5-5", name: "GPT-5.5" }]) }),
}));
vi.mock("@/composables/use-harnesses", () => ({
  useHarnesses: () => ({ harnesses: shallowRef([{ type: "claude-code", displayName: "Claude Code" }, { type: "opencode2", displayName: "OpenCode 2" }]) }),
}));
// A selected agent's activity comes from its own session's stream.
vi.mock("@/composables/use-session-stream", () => ({
  useSessionStream: (sessionId: MaybeRefOrGetter<string>) => ({
    messages: computed(() => childMessages[toValue(sessionId)] ?? []),
    isLoading: shallowRef(false),
  }),
}));
vi.mock("@tanstack/vue-router", () => ({ useRouter: () => ({ navigate }) }));

import AgentsCanvas from "@/components/canvas/AgentsCanvas.vue";
import { _resetRunningWorkForTesting, publishRunningWork } from "@/composables/use-running-work";
import { _resetSessionLineageForTesting } from "@/composables/use-session-lineage";
import { toRunningWorkItem, type RunningWorkItem } from "@/lib/running-work";
import { useSessionsStore } from "@/stores/sessions";

const T0 = Date.parse("2026-10-04T10:03:20Z");

function session(id: string, title: string, extra: Partial<SessionListItem> = {}): SessionListItem {
  return {
    instanceId: `i-${id}`, workspaceId: "w", workspaceDirectory: "/repo", workspaceDisplayName: null,
    isolationStrategy: "existing", sessionStatus: "idle", session: { id, title } as SessionListItem["session"],
    instanceStatus: "running", lifecycleStatus: "running", retentionStatus: "active", typedInstanceStatus: "running",
    isHidden: false, tags: [], harnessType: "claude-code", ...extra,
  };
}

/** Server-shaped items, as OpenCode 2 reports a subagent (Stop, no output) and Pi's extension one (neither). */
const reviewer = {
  id: "w-review", sessionId: "s1", workId: "call_review", kind: "subagent", title: "code-reviewer", label: "Review the diff",
  status: "running", background: true, childSessionId: "child-review", toolCallId: "call_review", canStop: true,
  startedAt: "2026-10-04T10:01:40Z",
};
const piSubagent = {
  id: "w-pi", sessionId: "s1", workId: "call_pi", kind: "subagent", title: "scout", label: "Map the repo",
  status: "running", toolCallId: "call_pi", startedAt: "2026-10-04T10:03:00Z",
};
const explore = {
  id: "w-explore", sessionId: "s1", workId: "call_explore", kind: "subagent", title: "Explore",
  label: "Find where subagent lines are dropped", status: "completed", childSessionId: "child-explore",
  startedAt: "2026-10-04T09:50:00Z", endedAt: "2026-10-04T09:50:38Z", endedReason: "completed",
};
const shell = {
  id: "w-shell", sessionId: "s1", workId: "sh_1", kind: "shell", title: "shell", label: "bun run test:e2e",
  status: "running", canStop: true, canReadOutput: true, startedAt: "2026-10-04T10:00:00Z",
};

function items(...payloads: Record<string, unknown>[]): RunningWorkItem[] {
  return payloads.map((payload) => toRunningWorkItem(payload)!);
}

function seedSessions(): void {
  useSessionsStore().setSessions([
    session("s1", "Capture Claude Code subagents", { spawnedBySessionId: "p1", spawnKind: "api", selectedModel: { providerID: "anthropic", modelID: "claude-opus-5-5" } }),
    session("p1", "What can we learn from t3code?", { projectName: "tidytempo" }),
    session("fork-1", "Fork: emit jobs over SignalR", { forkedFromSessionId: "s1", spawnKind: "fork", sessionStatus: "waiting_input", harnessType: "opencode2", lastAssistantModelId: "gpt-5-5" }),
    session("started-1", "Fix Pi model switch", { spawnedBySessionId: "s1", spawnKind: "api" }),
  ]);
}

function mountCanvas() {
  return mount(AgentsCanvas, { props: { sessionId: "s1" } });
}

function rows(wrapper: ReturnType<typeof mountCanvas>, section: string) {
  return wrapper.get(`[data-testid='agents-${section}']`).findAll("[data-testid='agents-row']");
}

describe("AgentsCanvas", () => {
  beforeEach(() => {
    vi.useFakeTimers({ toFake: ["Date", "setInterval", "clearInterval"] });
    vi.setSystemTime(T0);
    _resetRunningWorkForTesting();
    _resetSessionLineageForTesting();
    for (const key of Object.keys(childMessages)) delete childMessages[key];
    apiGet.mockReset();
    apiPost.mockReset();
    apiFetch.mockReset();
    navigate.mockReset();
    apiGet.mockResolvedValue({ data: [], response: { ok: true, status: 200 } });
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it("shows the parent, what runs now, the sessions it started, and earlier agents folded", async () => {
    seedSessions();
    publishRunningWork("s1", items(reviewer, shell));
    // Everything the session ever ran, for Earlier agents.
    apiGet.mockResolvedValue({ data: [reviewer, shell, explore], response: { ok: true, status: 200 } });

    const wrapper = mountCanvas();
    await flushPromises();

    expect(apiGet).toHaveBeenCalledWith("/api/sessions/{id}/work", { params: { path: { id: "s1" }, query: { all: true } } });
    expect(wrapper.get("[data-testid='agents-summary']").text()).toBe("1 running · 1 waiting for you");

    const parent = rows(wrapper, "parent");
    expect(parent).toHaveLength(1);
    expect(parent[0]!.text()).toContain("What can we learn from t3code?");
    expect(parent[0]!.get(".agent-row__runs-on").text()).toBe("tidytempo · Claude Code");
    expect(parent[0]!.get(".agent-row__note").text()).toBe("· started this session");

    // Shells stay in the background strip: only agents here.
    const running = rows(wrapper, "running");
    expect(running.map((row) => row.attributes("data-kind"))).toEqual(["subagent", "fork"]);
    expect(running[0]!.text()).toContain("code-reviewer");
    expect(running[0]!.text()).toContain("Review the diff");
    expect(running[0]!.text()).toContain("1m");
    expect(running[1]!.text()).toContain("needs you");
    expect(running[1]!.get(".agent-row__runs-on").text()).toBe("OpenCode 2 · GPT-5.5");
    expect(running[1]!.get(".agent-row__note").text()).toBe("· asked a question");

    const started = rows(wrapper, "started");
    expect(started.map((row) => row.text())).toEqual([expect.stringContaining("Fix Pi model switch")]);

    const toggle = wrapper.get("[data-testid='agents-earlier-toggle']");
    expect(toggle.text()).toBe("Earlier agents · 1");
    expect(toggle.attributes("aria-expanded")).toBe("false");
    expect(wrapper.get("[data-testid='agents-earlier']").findAll("[data-testid='agents-row']")).toHaveLength(0);

    await toggle.trigger("click");
    const earlier = rows(wrapper, "earlier");
    expect(earlier).toHaveLength(1);
    expect(earlier[0]!.attributes("data-state")).toBe("done");
    expect(earlier[0]!.text()).toContain("38s");
  });

  it("opens the first running agent's detail: what it was asked, tools, tokens, latest output, Open session and Stop", async () => {
    seedSessions();
    publishRunningWork("s1", items(reviewer));
    childMessages["child-review"] = [
      { messageId: "u", sessionId: "child-review", role: "user", parts: [{ partId: "t", type: "text", text: "Review the diff for the subagent mapper" }] },
      {
        messageId: "a", sessionId: "child-review", role: "assistant", modelID: "claude-sonnet-5-5",
        tokens: { input: 38_200, output: 1_900, reasoning: 0 },
        parts: [
          { partId: "1", type: "tool", tool: "read", callId: "1", state: {} },
          { partId: "2", type: "tool", tool: "grep", callId: "2", state: {} },
          { partId: "3", type: "text", text: "Reading OpenCode2Delegations.cs to compare status handling…" },
        ],
      },
    ];

    const wrapper = mountCanvas();
    await flushPromises();

    const detail = wrapper.get("[data-testid='agents-detail']");
    expect(detail.text()).toContain("Asked toReview the diff");
    expect(detail.get("[data-testid='agents-detail-tools']").text()).toBe("2 calls · Read, Grep");
    expect(detail.get("[data-testid='agents-detail-tokens']").text()).toBe("38.2k in · 1.9k out");
    expect(detail.get("[data-testid='agents-detail-latest']").text()).toBe("Reading OpenCode2Delegations.cs to compare status handling…");
    // The model its session answered with names it on the row.
    expect(rows(wrapper, "running")[0]!.text()).toContain("Claude Code · Sonnet 5.5");

    const open = detail.get("[data-testid='agents-open-session']");
    expect(open.attributes("href")).toBe("/sessions/child-review?instanceId=child-review&parentSessionId=s1");
    await open.trigger("click", { button: 0 });
    expect(navigate).toHaveBeenCalledWith({
      to: "/sessions/$id", params: { id: "child-review" }, search: { instanceId: "child-review", parentSessionId: "s1" },
    });

    apiPost.mockResolvedValue({
      data: { ...reviewer, status: "cancelled", endedAt: "2026-10-04T10:03:21Z", endedReason: "cancelled" },
      response: { ok: true, status: 200 },
    });
    await detail.get("[data-testid='agents-stop']").trigger("click");
    await flushPromises();
    expect(apiPost).toHaveBeenCalledWith("/api/sessions/{id}/work/{workId}/stop", { params: { path: { id: "s1", workId: "w-review" } } });
    // Stopped, it moves to Earlier agents.
    expect(wrapper.find("[data-testid='agents-running']").exists()).toBe(true);
    expect(rows(wrapper, "running").map((row) => row.attributes("data-kind"))).toEqual(["fork"]);
  });

  it("offers only what the harness allows: no Open or Stop for a subagent without a session or a stop", async () => {
    useSessionsStore().setSessions([session("s1", "Pi session", { harnessType: "pi" })]);
    publishRunningWork("s1", items(piSubagent));

    const wrapper = mountCanvas();
    await flushPromises();

    const detail = wrapper.get("[data-testid='agents-detail']");
    expect(detail.text()).toContain("Map the repo");
    expect(detail.find("[data-testid='agents-open-session']").exists()).toBe(false);
    expect(detail.find("[data-testid='agents-stop']").exists()).toBe(false);
    expect(detail.text()).toContain("Interrupt the turn to stop it.");
    expect(rows(wrapper, "running")[0]!.text()).toContain("no session of its own");
  });

  it("selecting another row shows its detail; selecting it again closes it", async () => {
    seedSessions();
    publishRunningWork("s1", items(reviewer));
    const wrapper = mountCanvas();
    await flushPromises();

    const fork = rows(wrapper, "running")[1]!;
    await fork.trigger("click");
    expect(fork.attributes("aria-expanded")).toBe("true");
    expect(wrapper.get("[data-testid='agents-open-session']").attributes("href")).toBe("/sessions/fork-1?instanceId=i-fork-1");
    expect(wrapper.find("[data-testid='agents-stop']").exists()).toBe(false);

    await fork.trigger("click");
    expect(wrapper.find("[data-testid='agents-detail']").exists()).toBe(false);
  });

  it("fetches the parent's title when the list doesn't have it, and opens it", async () => {
    useSessionsStore().setSessions([session("s1", "Fork", { forkedFromSessionId: "gone", spawnKind: "fork" })]);
    apiFetch.mockResolvedValue({ ok: true, json: async () => ({ title: "An archived session" }) });

    const wrapper = mountCanvas();
    await flushPromises();

    const parent = rows(wrapper, "parent")[0]!;
    expect(parent.text()).toContain("An archived session");
    expect(parent.text()).toContain("forked into this session");
    await parent.trigger("click");
    expect(navigate).toHaveBeenCalledWith({ to: "/sessions/$id", params: { id: "gone" }, search: { instanceId: undefined, parentSessionId: undefined } });
  });

  it("says what it will show when there's nothing yet", async () => {
    useSessionsStore().setSessions([session("s1", "Plain")]);
    const wrapper = mountCanvas();
    await flushPromises();

    expect(wrapper.text()).toContain("Subagents this session's agent starts");
  });
});
