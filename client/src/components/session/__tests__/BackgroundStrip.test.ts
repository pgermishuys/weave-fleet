import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { flushPromises, mount } from "@vue/test-utils";
import { shallowRef } from "vue";
import type { SessionListItem } from "@/api/client";

const { apiGet, apiPost, navigate } = vi.hoisted(() => ({
  apiGet: vi.fn(),
  apiPost: vi.fn(),
  navigate: vi.fn(),
}));

vi.mock("@/api/client", () => ({ api: { GET: apiGet, POST: apiPost } }));
vi.mock("@/composables/use-signalr-socket", () => ({
  onGlobalEvent: () => () => undefined,
  onReconnect: () => () => undefined,
}));
vi.mock("@/composables/use-models", () => ({
  useModels: () => ({ models: shallowRef([{ id: "claude-sonnet-5-5", name: "Sonnet 5.5" }]) }),
}));
vi.mock("@tanstack/vue-router", () => ({ useRouter: () => ({ navigate }) }));

import BackgroundStrip from "@/components/session/BackgroundStrip.vue";
import { _resetRunningWorkForTesting, publishRunningWork } from "@/composables/use-running-work";
import { toRunningWorkItem, type RunningWorkItem } from "@/lib/running-work";
import { useSessionsStore } from "@/stores/sessions";

const T0 = Date.parse("2026-10-04T10:03:12Z");

/** Server-shaped items, as each harness reports them (see the harness table in docs/background-work-and-lineage.md). */
const oc2Shell = {
  id: "w-shell", sessionId: "s1", workId: "sh_1", kind: "shell", title: "shell", label: "bun run test:e2e",
  status: "running", background: true, toolCallId: "call_bg", canStop: true, canReadOutput: true,
  startedAt: "2026-10-04T10:00:00Z",
};
const oc2Subagent = {
  id: "w-agent", sessionId: "s1", workId: "call_sub", kind: "subagent", title: "code-reviewer", label: "Review the diff",
  status: "running", background: true, childSessionId: "child-1", toolCallId: "call_sub", canStop: true,
  startedAt: "2026-10-04T10:01:32Z",
};
/** OpenCode's subagent: Stop (abort the child), but no output of its own. */
const oc1Subagent = { ...oc2Subagent, id: "w-oc1", canStop: true, canReadOutput: false };
/** A Claude Code monitor as PR 6 will report it: no per-task stop yet, so it can't be stopped on its own. */
const ccMonitor = {
  id: "w-monitor", sessionId: "s1", workId: "task_1", kind: "monitor", title: "Monitor", label: "Watch fleet.log for errors",
  status: "running", background: true, toolCallId: "call_monitor", canReadOutput: true, startedAt: "2026-10-04T10:02:14Z",
};
/** Pi's subagent extension: runs inside the tool call, no session, no Stop or Output of its own (PR 7). */
const piSubagent = {
  id: "w-pi", sessionId: "s1", workId: "call_pi:0", kind: "subagent", title: "reviewer", label: "Review the diff",
  status: "running", background: false, toolCallId: "call_pi", canStop: false, canReadOutput: false,
  startedAt: "2026-10-04T10:01:32Z", detail: "step 1 of 2 · kimi-k2.5",
};
const finishedLint = {
  ...oc2Shell, id: "w-lint", label: "bun run lint", status: "completed", endedAt: "2026-10-04T10:02:52Z",
  endedReason: "completed", detail: "exit 0", startedAt: "2026-10-04T10:02:30Z",
};

function items(...payloads: Record<string, unknown>[]): RunningWorkItem[] {
  return payloads.map((payload) => toRunningWorkItem(payload)!);
}

function mountStrip() {
  return mount(BackgroundStrip, { props: { sessionId: "s1" } });
}

function rows(wrapper: ReturnType<typeof mountStrip>) {
  return wrapper.findAll("[data-testid='background-work-row']");
}

describe("BackgroundStrip", () => {
  beforeEach(() => {
    vi.useFakeTimers({ toFake: ["Date", "setInterval", "clearInterval"] });
    vi.setSystemTime(T0);
    _resetRunningWorkForTesting();
    globalThis.localStorage?.clear();
    apiGet.mockReset();
    apiPost.mockReset();
    navigate.mockReset();
    apiGet.mockResolvedValue({ data: [], response: { ok: true, status: 200 } });
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it("shows nothing when nothing runs", async () => {
    publishRunningWork("s1", []);
    const wrapper = mountStrip();
    await flushPromises();

    expect(wrapper.find("[data-testid='background-strip']").exists()).toBe(false);
  });

  it("lists running work with kind, what it is, elapsed time, Output and Stop", async () => {
    publishRunningWork("s1", items(oc2Shell, oc2Subagent));
    const sessions = useSessionsStore();
    sessions.setSessions([
      { session: { id: "child-1", title: "Review" }, instanceId: "i-child", selectedModel: { providerID: "anthropic", modelID: "claude-sonnet-5-5" } } as unknown as SessionListItem,
    ]);
    const wrapper = mountStrip();
    await flushPromises();

    expect(wrapper.get("[data-testid='background-strip-summary']").text()).toBe("2 running");
    const [shellRow, agentRow] = rows(wrapper);
    expect(shellRow!.text()).toContain("Shell");
    expect(shellRow!.get("code").text()).toBe("bun run test:e2e");
    expect(shellRow!.text()).toContain("3m 12s");
    expect(shellRow!.find("[data-testid='background-work-output']").exists()).toBe(true);
    expect(shellRow!.get("[data-testid='background-work-stop']").attributes("aria-label")).toBe("Stop bun run test:e2e");
    // A shell runs in no session of its own.
    expect(shellRow!.find("[data-testid='background-work-open']").exists()).toBe(false);

    expect(agentRow!.text()).toContain("Agent");
    expect(agentRow!.text()).toContain("code-reviewer · Review the diff");
    expect(agentRow!.get("[data-testid='background-work-model']").text()).toContain("Sonnet 5.5");
    expect(agentRow!.text()).toContain("1m 40s");
    expect(agentRow!.get("[data-testid='background-work-open']").attributes("href")).toBe("/sessions/child-1?instanceId=i-child&parentSessionId=s1");
    expect(agentRow!.get("[data-testid='background-work-stop']").attributes("aria-label")).toBe("Stop Review the diff");
    expect(agentRow!.find("[data-testid='background-work-output']").exists()).toBe(false);

    vi.advanceTimersByTime(2_000);
    await flushPromises();
    expect(shellRow!.text()).toContain("3m 14s");
  });

  it("offers no Output when the output can't be read, and no Stop when it can't be stopped on its own", async () => {
    publishRunningWork("s1", items(oc1Subagent, { ...ccMonitor }));
    const wrapper = mountStrip();
    await flushPromises();

    const [agentRow, monitorRow] = rows(wrapper);
    expect(agentRow!.find("[data-testid='background-work-output']").exists()).toBe(false);
    expect(agentRow!.find("[data-testid='background-work-stop']").exists()).toBe(true);
    expect(monitorRow!.find("[data-testid='background-work-stop']").exists()).toBe(false);
    expect(monitorRow!.find("[data-testid='background-work-events']").exists()).toBe(true);
  });

  it("shows finished work with its result and no Stop, then drops it", async () => {
    publishRunningWork("s1", items(oc2Shell));
    const wrapper = mountStrip();
    await flushPromises();
    publishRunningWork("s1", items(finishedLint, oc2Shell));
    await flushPromises();

    // finishedLint ended before this browser saw it run, 20 s ago by the server's clock.
    expect(wrapper.get("[data-testid='background-strip-summary']").text()).toBe("1 running · 1 finished");
    const finished = rows(wrapper).find((row) => row.attributes("data-status") === "ended")!;
    expect(finished.get("[data-testid='background-work-result']").text()).toContain("exit 0");
    expect(finished.text()).toContain("20s ago");
    expect(finished.find("[data-testid='background-work-stop']").exists()).toBe(false);
    expect(finished.find("[data-testid='background-work-output']").exists()).toBe(true);

    vi.advanceTimersByTime(11_000);
    await flushPromises();
    expect(wrapper.get("[data-testid='background-strip-summary']").text()).toBe("1 running");
  });

  it("collapses to its header line and remembers it", async () => {
    publishRunningWork("s1", items(oc2Shell));
    const wrapper = mountStrip();
    await flushPromises();

    const toggle = wrapper.get("[data-testid='background-strip-toggle']");
    expect(toggle.attributes("aria-expanded")).toBe("true");
    await toggle.trigger("click");
    expect(rows(wrapper)).toHaveLength(0);
    expect(toggle.attributes("aria-expanded")).toBe("false");
    expect(mountStrip().findAll("[data-testid='background-work-row']")).toHaveLength(0);
  });

  it("stops an item and shows it stopped", async () => {
    publishRunningWork("s1", items(oc2Shell));
    apiPost.mockResolvedValue({
      data: { ...oc2Shell, status: "cancelled", endedAt: "2026-10-04T10:03:12Z", endedReason: "cancelled", detail: "stopped" },
      response: { ok: true, status: 200 },
    });
    const wrapper = mountStrip();
    await flushPromises();

    await wrapper.get("[data-testid='background-work-stop']").trigger("click");
    await flushPromises();

    expect(apiPost).toHaveBeenCalledWith("/api/sessions/{id}/work/{workId}/stop", { params: { path: { id: "s1", workId: "w-shell" } } });
    expect(wrapper.get("[data-testid='background-work-result']").text()).toContain("stopped");
    expect(wrapper.find("[data-testid='background-work-stop']").exists()).toBe(false);
  });

  it("says why a stop was refused", async () => {
    publishRunningWork("s1", items(oc2Shell));
    apiPost.mockResolvedValue({ error: { error: "The session isn't running." }, response: { ok: false, status: 409 } });
    const wrapper = mountStrip();
    await flushPromises();

    await wrapper.get("[data-testid='background-work-stop']").trigger("click");
    await flushPromises();

    expect(wrapper.get("[role='alert']").text()).toBe("The session isn't running.");
  });

  it("opens the tail of the output under the row", async () => {
    publishRunningWork("s1", items(oc2Shell));
    const wrapper = mountStrip();
    await flushPromises();
    apiGet.mockResolvedValue({ data: { output: "✓ 12 tests passed\n", nextOffset: 19, size: 19, truncated: false }, response: { ok: true, status: 200 } });

    await wrapper.get("[data-testid='background-work-output']").trigger("click");
    await flushPromises();

    expect(apiGet).toHaveBeenCalledWith("/api/sessions/{id}/work/{workId}/output", {
      params: { path: { id: "s1", workId: "w-shell" }, query: { offset: 0 } },
    });
    expect(wrapper.get("[data-testid='work-output']").text()).toContain("✓ 12 tests passed");
  });

  it("opens a subagent's session", async () => {
    publishRunningWork("s1", items(oc2Subagent));
    const wrapper = mountStrip();
    await flushPromises();

    await wrapper.get("[data-testid='background-work-open']").trigger("click", { button: 0 });
    expect(navigate).toHaveBeenCalledWith({
      to: "/sessions/$id",
      params: { id: "child-1" },
      search: { instanceId: "child-1", parentSessionId: "s1" },
    });
  });

  it("jumps to a monitor's events in the conversation", async () => {
    publishRunningWork("s1", items(ccMonitor));
    const listener = vi.fn();
    window.addEventListener("weave:command-show-message", listener);
    const wrapper = mountStrip();
    await flushPromises();

    await wrapper.get("[data-testid='background-work-events']").trigger("click");
    window.removeEventListener("weave:command-show-message", listener);

    expect((listener.mock.calls[0]![0] as CustomEvent).detail).toEqual({ sessionId: "s1", toolCallId: "call_monitor" });
  });

  it("gives a subagent with no session of its own Details instead of Open, and says what it is doing", async () => {
    publishRunningWork("s1", items(piSubagent));
    const wrapper = mountStrip();
    await flushPromises();

    const [row] = rows(wrapper);
    expect(row!.text()).toContain("reviewer · Review the diff");
    expect(row!.get("[data-testid='background-work-detail']").text()).toBe("· step 1 of 2 · kimi-k2.5");
    for (const action of ["open", "output", "stop"]) {
      expect(row!.find(`[data-testid='background-work-${action}']`).exists()).toBe(false);
    }

    await row!.get("[data-testid='background-work-details']").trigger("click");
    const panel = row!.get("[data-testid='background-work-details-panel']");
    expect(panel.text()).toContain("Asked to");
    expect(panel.text()).toContain("Review the diff");
    expect(panel.text()).toContain("Now");
    expect(panel.text()).toContain("step 1 of 2 · kimi-k2.5");
    expect(panel.text()).toContain("Runs inside the tool call. Interrupt the turn to stop it.");

    await row!.get("[data-testid='background-work-details']").trigger("click");
    expect(row!.find("[data-testid='background-work-details-panel']").exists()).toBe(false);
  });

  it("shows a finished subagent's result once, and offers no Details for one with a session", async () => {
    publishRunningWork("s1", items(
      { ...piSubagent, status: "completed", endedAt: "2026-10-04T10:03:00Z", endedReason: "completed", detail: "step 1 of 2 · kimi-k2.5 · No problems found." },
      oc2Subagent,
    ));
    const wrapper = mountStrip();
    await flushPromises();

    // Running work lists first.
    const [oc2Row, piRow] = rows(wrapper);
    expect(piRow!.find("[data-testid='background-work-detail']").exists()).toBe(false);
    expect(piRow!.get("[data-testid='background-work-result']").text()).toBe("· step 1 of 2 · kimi-k2.5 · No problems found.");
    await piRow!.get("[data-testid='background-work-details']").trigger("click");
    expect(piRow!.get("[data-testid='background-work-details-panel']").text()).toContain("Ended");
    expect(piRow!.get("[data-testid='background-work-details-panel']").text()).not.toContain("Interrupt the turn");

    expect(oc2Row!.find("[data-testid='background-work-details']").exists()).toBe(false);
  });
});
