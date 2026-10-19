import { beforeEach, describe, expect, it, vi } from "vitest";
import type { DomainEvent } from "@/lib/domain-events";
import { flushAll, mountComposable } from "./test-utils";

const { subscribeV2Mock, getMock, deleteMock, reconnectCallbacks } = vi.hoisted(() => ({
  subscribeV2Mock: vi.fn(),
  getMock: vi.fn(),
  deleteMock: vi.fn(),
  reconnectCallbacks: [] as (() => void)[],
}));

vi.mock("@/api/client", () => ({ api: { GET: getMock, DELETE: deleteMock } }));
vi.mock("@/composables/use-weave-socket", () => ({
  useWeaveSocket: () => ({ subscribeV2: subscribeV2Mock }),
  onReconnect: (_machine: unknown, callback: () => void) => {
    reconnectCallbacks.push(callback);
    return () => reconnectCallbacks.splice(reconnectCallbacks.indexOf(callback), 1);
  },
}));

type OnEvent = (event: DomainEvent) => void;
const listeners = new Map<string, OnEvent>();
let sessionCounter = 0;

const rule = (id: string, ...patterns: string[]) => ({ id, kind: "shell", tool: "bash", patterns, createdAt: "2026-10-19T09:00:00Z" });

async function mountRules() {
  // Rules are kept per session id at module level, so every test uses a session of its own.
  const sessionId = `rules-session-${++sessionCounter}`;
  const { useSessionPermissionRules } = await import("@/composables/use-session-permission-rules");
  const mounted = await mountComposable(() => useSessionPermissionRules(sessionId));
  return { ...mounted, sessionId, push: (event: DomainEvent) => listeners.get(`session:${sessionId}`)?.(event) };
}

describe("useSessionPermissionRules", () => {
  beforeEach(() => {
    subscribeV2Mock.mockReset().mockImplementation((topic: string, _onSnapshot: unknown, onEvent: OnEvent) => {
      listeners.set(topic, onEvent);
      return () => listeners.delete(topic);
    });
    getMock.mockReset().mockResolvedValue({ data: [], response: { ok: true } });
    deleteMock.mockReset().mockResolvedValue({ response: { ok: true, status: 204 } });
    reconnectCallbacks.length = 0;
    listeners.clear();
  });

  it("loads the rules when the session opens", async () => {
    getMock.mockResolvedValue({ data: [rule("rule_1", "bun test*")], response: { ok: true } });

    const { result } = await mountRules();

    expect(result.rules.value).toEqual([rule("rule_1", "bun test*")]);
  });

  it("takes the whole list a session.permission-rules event carries, and only for its own session", async () => {
    const { result, sessionId, push } = await mountRules();

    push({ type: "session.permission-rules", payload: { sessionId: "another", items: [rule("rule_x", "*")] } } as DomainEvent);
    expect(result.rules.value).toEqual([]);

    push({ type: "session.permission-rules", payload: { sessionId, items: [rule("rule_1", "a*"), rule("rule_2", "b*")] } } as DomainEvent);
    expect(result.rules.value.map((r) => r.id)).toEqual(["rule_1", "rule_2"]);
  });

  it("removes a rule at once and tells Fleet", async () => {
    getMock.mockResolvedValue({ data: [rule("rule_1", "a*"), rule("rule_2", "b*")], response: { ok: true } });
    const { result, sessionId } = await mountRules();

    await result.remove("rule_1");

    expect(result.rules.value.map((r) => r.id)).toEqual(["rule_2"]);
    expect(deleteMock).toHaveBeenCalledWith("/api/sessions/{id}/permission-rules/{ruleId}", { params: { path: { id: sessionId, ruleId: "rule_1" } } });
  });

  it("loads the list again when Fleet refuses a removal", async () => {
    getMock.mockResolvedValue({ data: [rule("rule_1", "a*")], response: { ok: true } });
    const { result } = await mountRules();
    deleteMock.mockResolvedValue({ response: { ok: false, status: 500 } });

    await result.remove("rule_1");
    await flushAll();

    expect(getMock).toHaveBeenCalledTimes(2);
    expect(result.rules.value.map((r) => r.id)).toEqual(["rule_1"]);
  });

  it("removes every rule", async () => {
    getMock.mockResolvedValue({ data: [rule("rule_1", "a*"), rule("rule_2", "b*")], response: { ok: true } });
    const { result, sessionId } = await mountRules();

    await result.removeAll();

    expect(result.rules.value).toEqual([]);
    expect(deleteMock).toHaveBeenCalledWith("/api/sessions/{id}/permission-rules", { params: { path: { id: sessionId } } });
  });
});
