import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { createPinia, setActivePinia } from "pinia";
import type { DomainEvent } from "@/lib/domain-events";
import { flushAll, mountComposable } from "./test-utils";

const { apiGet, apiPost, globalHandlers, reconnectHandlers } = vi.hoisted(() => ({
  apiGet: vi.fn(),
  apiPost: vi.fn(),
  globalHandlers: [] as Array<(event: DomainEvent) => void>,
  reconnectHandlers: [] as Array<() => void>,
}));

vi.mock("@/api/client", () => ({ api: { GET: apiGet, POST: apiPost } }));
vi.mock("@/composables/use-signalr-socket", () => ({
  onGlobalEvent: (_topic: string, handler: (event: DomainEvent) => void) => {
    globalHandlers.push(handler);
    return () => undefined;
  },
  onReconnect: (handler: () => void) => {
    reconnectHandlers.push(handler);
    return () => undefined;
  },
}));

import {
  FINISHED_VISIBLE_MS,
  _resetRunningWorkForTesting,
  publishRunningWork,
  useRunningWork,
  useRunningWorkAcrossSessions,
} from "@/composables/use-running-work";
import { toRunningWorkItem, type RunningWorkItem } from "@/lib/running-work";

const T0 = Date.parse("2026-10-04T10:05:00Z");

/** Server-shaped payloads (nulls and false left out), as in SignalREventContractTests. */
function shell(sessionId = "s1", id = "w-shell", extra: Record<string, unknown> = {}) {
  return {
    id, sessionId, workId: "sh_1", kind: "shell", title: "shell", label: "bun run test:e2e", status: "running",
    background: true, toolCallId: "call_bg", canStop: true, canReadOutput: true, startedAt: "2026-10-04T10:00:00Z", ...extra,
  };
}

function ended(payload: Record<string, unknown>, at = "2026-10-04T10:05:00Z") {
  return { ...payload, status: "completed", endedAt: at, endedReason: "completed", detail: "exit 0" };
}

function subagent(sessionId = "s2", id = "w-agent") {
  return {
    id, sessionId, workId: "call_sub", kind: "subagent", title: "code-reviewer", label: "Review the diff", status: "running",
    background: true, childSessionId: "child-1", toolCallId: "call_sub", canStop: true, startedAt: "2026-10-04T10:02:00Z",
  };
}

function items(...payloads: Record<string, unknown>[]): RunningWorkItem[] {
  return payloads.map((payload) => toRunningWorkItem(payload)!);
}

function emitOnSessions(type: "work.started" | "work.updated" | "work.ended", payload: Record<string, unknown>): void {
  for (const handler of globalHandlers) handler({ type, payload: payload as never });
}

function ok(data: unknown) {
  return { data, error: undefined, response: { ok: true, status: 200 } };
}

describe("useRunningWork", () => {
  beforeEach(() => {
    vi.useFakeTimers({ toFake: ["Date", "setInterval", "clearInterval", "setTimeout", "clearTimeout"] });
    vi.setSystemTime(T0);
    setActivePinia(createPinia());
    _resetRunningWorkForTesting();
    globalHandlers.length = 0;
    reconnectHandlers.length = 0;
    apiGet.mockReset();
    apiPost.mockReset();
    apiGet.mockResolvedValue(ok([]));
  });

  afterEach(() => {
    vi.useRealTimers();
  });

  it("loads the session's work when no stream has said yet", async () => {
    apiGet.mockResolvedValue(ok([shell()]));
    const { result } = await mountComposable(() => useRunningWork("s1"));

    expect(apiGet).toHaveBeenCalledWith("/api/sessions/{id}/work", { params: { path: { id: "s1" }, query: {} } });
    expect(result.running.value.map((item) => item.id)).toEqual(["w-shell"]);
    expect(result.visible.value).toHaveLength(1);
  });

  it("takes the stream's work and doesn't load what the stream already has", async () => {
    publishRunningWork("s1", items(shell()));
    const { result } = await mountComposable(() => useRunningWork("s1"));

    expect(apiGet).not.toHaveBeenCalled();
    expect(result.running.value).toHaveLength(1);
  });

  it("follows work.* events on the sessions topic", async () => {
    const { result } = await mountComposable(() => useRunningWork("s1"));

    emitOnSessions("work.started", shell());
    emitOnSessions("work.started", shell("s2", "elsewhere"));
    await flushAll();
    expect(result.running.value.map((item) => item.id)).toEqual(["w-shell"]);

    emitOnSessions("work.ended", ended(shell()));
    await flushAll();
    expect(result.running.value).toEqual([]);
    expect(result.finished.value.map((item) => item.detail)).toEqual(["exit 0"]);
  });

  it("keeps a finished item with its result for a while, then drops it, counting from when this browser saw it end", async () => {
    publishRunningWork("s1", items(shell()));
    const { result } = await mountComposable(() => useRunningWork("s1"));

    // The server's clock is ten minutes behind: its endedAt alone would have hidden the result at once.
    publishRunningWork("s1", items(ended(shell(), "2026-10-04T09:55:00Z")));
    await flushAll();
    expect(result.visible.value.map((item) => item.status)).toEqual(["completed"]);

    vi.advanceTimersByTime(FINISHED_VISIBLE_MS - 2_000);
    await flushAll();
    expect(result.visible.value).toHaveLength(1);

    vi.advanceTimersByTime(3_000);
    await flushAll();
    expect(result.visible.value).toEqual([]);
    expect(result.items.value).toHaveLength(1);
  });

  it("doesn't show work that had long ended when the session opened", async () => {
    apiGet.mockResolvedValue(ok([ended(shell(), "2026-10-04T10:00:30Z")]));
    const { result } = await mountComposable(() => useRunningWork("s1"));

    expect(result.items.value).toHaveLength(1);
    expect(result.visible.value).toEqual([]);
  });

  it("ticks elapsed time every second while something runs", async () => {
    publishRunningWork("s1", items(shell()));
    const { result } = await mountComposable(() => useRunningWork("s1"));
    const before = result.now.value;

    vi.advanceTimersByTime(3_000);
    expect(result.now.value - before).toBe(3_000);
  });

  it("stops an item through Fleet and shows it ended at once", async () => {
    publishRunningWork("s1", items(shell()));
    apiPost.mockResolvedValue(ok({ ...shell(), status: "cancelled", endedAt: "2026-10-04T10:05:00Z", endedReason: "cancelled", detail: "stopped" }));
    const { result } = await mountComposable(() => useRunningWork("s1"));

    const outcome = await result.stop("w-shell");
    await flushAll();

    expect(apiPost).toHaveBeenCalledWith("/api/sessions/{id}/work/{workId}/stop", { params: { path: { id: "s1", workId: "w-shell" } } });
    expect(outcome.ok).toBe(true);
    expect(result.running.value).toEqual([]);
    expect(result.finished.value[0]).toMatchObject({ status: "cancelled", detail: "stopped" });
  });

  it("says why Fleet refused to stop it", async () => {
    publishRunningWork("s1", items(shell()));
    apiPost.mockResolvedValue({ data: undefined, error: { error: "It has already ended." }, response: { ok: false, status: 409 } });
    const { result } = await mountComposable(() => useRunningWork("s1"));

    expect(await result.stop("w-shell")).toEqual({ ok: false, error: "It has already ended." });
    expect(result.running.value).toHaveLength(1);
  });

  it("reads a page of output from an offset", async () => {
    apiGet.mockResolvedValue(ok({ output: "PASS 12 tests\n", nextOffset: 14, size: 14, truncated: false }));
    const { result } = await mountComposable(() => useRunningWork("s1"));

    const page = await result.readOutput("w-shell", 0);
    expect(apiGet).toHaveBeenLastCalledWith("/api/sessions/{id}/work/{workId}/output", {
      params: { path: { id: "s1", workId: "w-shell" }, query: { offset: 0 } },
    });
    expect(page).toEqual({ ok: true, page: { output: "PASS 12 tests\n", nextOffset: 14, size: 14, truncated: false } });
  });

  it("loads again after a reconnect", async () => {
    await mountComposable(() => useRunningWork("s1"));
    apiGet.mockClear();

    for (const handler of reconnectHandlers) handler();
    await flushAll();
    expect(apiGet).toHaveBeenCalledWith("/api/sessions/{id}/work", expect.anything());
  });
});

describe("useRunningWorkAcrossSessions", () => {
  beforeEach(() => {
    setActivePinia(createPinia());
    _resetRunningWorkForTesting();
    globalHandlers.length = 0;
    reconnectHandlers.length = 0;
    apiGet.mockReset();
  });

  it("loads what runs everywhere and groups it by session", async () => {
    apiGet.mockResolvedValue(ok([shell("s1"), subagent("s2"), shell("s1", "w-shell-2")]));
    const { result } = await mountComposable(() => useRunningWorkAcrossSessions());

    expect(apiGet).toHaveBeenCalledWith("/api/work/running");
    expect(result.running.value).toHaveLength(3);
    expect(result.sessionCount.value).toBe(2);
    expect(result.groups.value.map((group) => [group.sessionId, group.items.length])).toEqual([["s1", 2], ["s2", 1]]);
  });

  it("counts work as it starts and stops counting it when it ends, without asking again", async () => {
    apiGet.mockResolvedValue(ok([]));
    const { result } = await mountComposable(() => useRunningWorkAcrossSessions());
    expect(result.running.value).toEqual([]);

    emitOnSessions("work.started", shell("s1"));
    emitOnSessions("work.started", subagent("s2"));
    await flushAll();
    expect(result.sessionCount.value).toBe(2);

    emitOnSessions("work.ended", ended(subagent("s2")));
    await flushAll();
    expect(result.running.value.map((item) => item.id)).toEqual(["w-shell"]);
    expect(apiGet).toHaveBeenCalledTimes(1);
  });
});
