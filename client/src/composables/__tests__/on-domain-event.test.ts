import { beforeEach, describe, expect, it, vi } from "vitest";
import type { DomainEvent } from "@/lib/domain-events";
import type { MachineTarget } from "@/lib/machine-target";

const { onGlobalEventMock } = vi.hoisted(() => ({ onGlobalEventMock: vi.fn() }));
vi.mock("@/composables/use-signalr-socket", () => ({ onGlobalEvent: onGlobalEventMock }));

import { onDomainEvent } from "@/composables/on-domain-event";

const machine = { key: "home", connection: null, isLive: true } as unknown as MachineTarget;

let deliver: (event: DomainEvent) => void;
let stop: ReturnType<typeof vi.fn>;

function event(type: string, payload: unknown): DomainEvent {
  return { type, payload } as unknown as DomainEvent;
}

describe("onDomainEvent", () => {
  beforeEach(() => {
    stop = vi.fn();
    onGlobalEventMock.mockReset().mockImplementation((_machine: unknown, _topic: string, callback: (event: DomainEvent) => void) => {
      deliver = callback;
      return stop;
    });
  });

  it("subscribes to the machine and topic it is given, and hands back the way to stop", () => {
    const off = onDomainEvent(machine, "sessions", "session_tokens", () => undefined);

    expect(onGlobalEventMock).toHaveBeenCalledWith(machine, "sessions", expect.any(Function));
    expect(stop).not.toHaveBeenCalled();
    off();
    expect(stop).toHaveBeenCalledOnce();
  });

  it("calls the handler only for events of its type, with the payload typed", () => {
    const totals: number[] = [];
    onDomainEvent(machine, "sessions", "session_tokens", (tokens) => {
      // `payload` is the session_tokens payload here, with no cast.
      totals.push(tokens.payload.totalTokens);
    });

    deliver(event("session_tokens", { sessionId: "s1", totalTokens: 120, totalCost: 0.01 }));
    deliver(event("session_progress", { sessionId: "s1", kind: "todos", done: 1, total: 2 }));
    deliver(event("session_tokens", { sessionId: "s2", totalTokens: 300, totalCost: 0.02 }));

    expect(totals).toEqual([120, 300]);
  });

  it("narrows each type to its own event", () => {
    const seen: string[] = [];
    onDomainEvent(machine, "session:s1", "session.retry", (retry) => {
      seen.push(`retry:${retry.payload.retry?.attempt ?? "none"}`);
    });

    deliver(event("session.retry", { sessionId: "s1", retry: { dueAt: "2026-10-09T10:00:00Z", attempt: 3, kind: "overloaded", reason: "", providerSaid: false } }));
    deliver(event("session.retry", { sessionId: "s1", retry: null }));
    deliver(event("session.queue", { sessionId: "s1", items: [] }));

    expect(seen).toEqual(["retry:3", "retry:none"]);
  });
});
