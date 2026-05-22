import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { shallowRef } from "vue";
import type { DomainEvent } from "@/lib/domain-events";
import { flushAll, mountComposable } from "./test-utils";

const { apiFetchMock, subscribeV2Mock } = vi.hoisted(() => ({
  apiFetchMock: vi.fn(),
  subscribeV2Mock: vi.fn(),
}));

let v2EventCallback: ((event: DomainEvent) => void) | null = null;

vi.mock("@/lib/api-client", () => ({
  apiFetch: apiFetchMock,
}));

vi.mock("@/composables/use-weave-socket", () => ({
  useWeaveSocket: () => ({
    subscribeV2: subscribeV2Mock,
  }),
}));

function createJsonResponse<T>(body: T, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "Content-Type": "application/json" },
  });
}

describe("useDiffs", () => {
  beforeEach(() => {
    v2EventCallback = null;
    apiFetchMock.mockReset();
    subscribeV2Mock.mockReset();
    subscribeV2Mock.mockImplementation((_topic: string, _onSnapshot: () => void, onEvent: (event: DomainEvent) => void) => {
      v2EventCallback = onEvent;
      return () => {
        v2EventCallback = null;
      };
    });
  });

  afterEach(() => {
    v2EventCallback = null;
  });

  it("fetches latest diffs when a turn ends for the current session", async () => {
    apiFetchMock.mockResolvedValue(createJsonResponse({
      diffs: [
        { file: "src/App.vue", status: "modified", additions: 3, deletions: 1 },
      ],
      available: true,
    }));

    const sessionId = shallowRef("session-1");
    const instanceId = shallowRef("instance-1");
    const { useDiffs } = await import("@/composables/use-diffs");
    const { result, wrapper } = await mountComposable(() => useDiffs(sessionId, instanceId));

    expect(subscribeV2Mock).toHaveBeenCalledWith("session:session-1", expect.any(Function), expect.any(Function));

    v2EventCallback?.({
      type: "turn.ended",
      payload: {
        sessionID: "session-1",
        messageID: "message-1",
        index: 0,
        reason: "completed",
        cost: 0,
        tokens: null,
        completedAt: 1,
      },
    });
    await flushAll();

    expect(apiFetchMock).toHaveBeenCalledTimes(1);
    expect(apiFetchMock).toHaveBeenCalledWith("/api/sessions/session-1/diffs?instanceId=instance-1");
    expect(result.diffs.value).toEqual([
      { file: "src/App.vue", status: "modified", additions: 3, deletions: 1 },
    ]);
    expect(result.available.value).toBe(true);
    expect(result.isStale.value).toBe(false);

    wrapper.unmount();
  });

  it("tracks_stale_state_until_the_next_successful_fetch", async () => {
    apiFetchMock.mockResolvedValue(createJsonResponse({
      diffs: [
        { file: "src/App.vue", status: "modified", additions: 1, deletions: 0 },
      ],
      available: true,
    }));

    const sessionId = shallowRef("session-1");
    const instanceId = shallowRef("instance-1");
    const { useDiffs } = await import("@/composables/use-diffs");
    const { result, wrapper } = await mountComposable(() => useDiffs(sessionId, instanceId));

    result.markStale();

    expect(result.isStale.value).toBe(true);

    await result.fetchDiffs();
    await flushAll();

    expect(result.isStale.value).toBe(false);

    wrapper.unmount();
  });

  it("clears_stale_state_when_session_or_instance_changes", async () => {
    const sessionId = shallowRef("session-1");
    const instanceId = shallowRef("instance-1");
    const { useDiffs } = await import("@/composables/use-diffs");
    const { result, wrapper } = await mountComposable(() => useDiffs(sessionId, instanceId));

    result.markStale();
    expect(result.isStale.value).toBe(true);

    sessionId.value = "session-2";
    await flushAll();

    expect(result.isStale.value).toBe(false);

    wrapper.unmount();
  });

  it("ignores turn ended events for other sessions", async () => {
    const sessionId = shallowRef("session-1");
    const instanceId = shallowRef("instance-1");
    const { useDiffs } = await import("@/composables/use-diffs");
    const { wrapper } = await mountComposable(() => useDiffs(sessionId, instanceId));

    v2EventCallback?.({
      type: "turn.ended",
      payload: {
        sessionID: "session-2",
        messageID: "message-1",
        index: 0,
        reason: "completed",
        cost: 0,
        tokens: null,
        completedAt: 1,
      },
    });
    await flushAll();

    expect(apiFetchMock).not.toHaveBeenCalled();

    wrapper.unmount();
  });

  it("exposes unavailable state from the diffs response", async () => {
    apiFetchMock.mockResolvedValue(createJsonResponse({
      diffs: [],
      available: false,
    }));

    const sessionId = shallowRef("session-1");
    const instanceId = shallowRef("instance-1");
    const { useDiffs } = await import("@/composables/use-diffs");
    const { result, wrapper } = await mountComposable(() => useDiffs(sessionId, instanceId));

    await result.fetchDiffs();
    await flushAll();

    expect(result.diffs.value).toEqual([]);
    expect(result.available.value).toBe(false);
    expect(result.error.value).toBeUndefined();

    wrapper.unmount();
  });

  it("documents that backend session diff summaries omit before and after content", async () => {
    const backendDiffSummary = {
      file: "src/App.vue",
      status: "modified",
      additions: 1,
      deletions: 1,
    };

    apiFetchMock.mockResolvedValue(createJsonResponse({
      diffs: [backendDiffSummary],
      available: true,
    }));

    const sessionId = shallowRef("session-1");
    const instanceId = shallowRef("instance-1");
    const { useDiffs } = await import("@/composables/use-diffs");
    const { result, wrapper } = await mountComposable(() => useDiffs(sessionId, instanceId));

    await result.fetchDiffs();
    await flushAll();

    const [diff] = result.diffs.value;
    expect(diff?.status).toBe("modified");
    expect(diff).toEqual(backendDiffSummary);
    expect(diff).not.toHaveProperty("before");
    expect(diff).not.toHaveProperty("after");

    // This intentionally verifies the real backend/frontend mismatch: GET
    // /api/sessions/{id}/diffs currently returns FileDiffSummary objects with
    // file/status/additions/deletions only. DiffView.vue integration cannot rely
    // on FileDiffItem.before/after from useDiffs; the plan must use or introduce
    // another source for populated diff content before wiring that view.
    expect(diff?.before).toBeUndefined();
    expect(diff?.after).toBeUndefined();

    wrapper.unmount();
  });
});
