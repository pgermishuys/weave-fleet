import { beforeEach, describe, expect, it, vi } from "vitest";
import type { DomainEvent } from "@/lib/domain-events";
import { flushAll, mountComposable } from "./test-utils";

const { getMock, onGlobalEventMock, onReconnectMock } = vi.hoisted(() => ({
  getMock: vi.fn(),
  onGlobalEventMock: vi.fn(),
  onReconnectMock: vi.fn(),
}));

vi.mock("@/api/client", () => ({ api: { GET: getMock } }));
vi.mock("@/composables/use-signalr-socket", () => ({ onGlobalEvent: onGlobalEventMock }));
vi.mock("@/composables/use-weave-socket", () => ({ onReconnect: onReconnectMock }));

let handler: ((event: DomainEvent) => void) | null = null;
let reconnect: (() => void) | null = null;

function usageWire(harnessType: string, utilization: number) {
  return { harnessType, windows: [{ window: "five_hour", utilization, resetsAt: "2026-10-09T15:00:00Z", status: "allowed" }] };
}

function send(type: string, payload: unknown): void {
  handler?.({ type, payload } as unknown as DomainEvent);
}

async function mountUsage() {
  const { useHarnessUsage, useHarnessUsageFor } = await import("@/composables/use-harness-usage");
  return mountComposable(() => ({ all: useHarnessUsage(), one: useHarnessUsageFor("claude-code") }));
}

describe("useHarnessUsage", () => {
  beforeEach(async () => {
    const { resetHarnessUsageForTests } = await import("@/composables/use-harness-usage");
    resetHarnessUsageForTests();
    handler = null;
    reconnect = null;
    getMock.mockReset().mockResolvedValue({ data: [usageWire("claude-code", 0.25)], response: { ok: true } });
    onGlobalEventMock.mockReset().mockImplementation((_machine: unknown, topic: string, callback: (event: DomainEvent) => void) => {
      expect(topic).toBe("sessions");
      handler = callback;
      return () => undefined;
    });
    onReconnectMock.mockReset().mockImplementation((_machine: unknown, callback: () => void) => {
      reconnect = callback;
      return () => undefined;
    });
  });

  it("loads each harness's limits once", async () => {
    const { result } = await mountUsage();
    await flushAll();

    expect(getMock).toHaveBeenCalledWith("/api/harnesses/usage");
    expect(result.all.usage["claude-code"]?.windows[0]?.utilization).toBe(0.25);
    expect(result.one.value?.harnessType).toBe("claude-code");
    expect(onGlobalEventMock).toHaveBeenCalledOnce();
  });

  it("follows pushed limits", async () => {
    const { result } = await mountUsage();
    await flushAll();

    send("harness.usage", usageWire("claude-code", 0.9));
    send("harness.usage", usageWire("pi", 0.1));

    expect(result.one.value?.windows[0]?.utilization).toBe(0.9);
    expect(result.all.usage["pi"]?.windows[0]?.utilization).toBe(0.1);
  });

  it("ignores other events and malformed payloads", async () => {
    const { result } = await mountUsage();
    await flushAll();

    send("session_tokens", usageWire("claude-code", 0.99));
    send("harness.usage", { harnessType: "claude-code" });
    send("harness.usage", null);

    expect(result.one.value?.windows[0]?.utilization).toBe(0.25);
  });

  it("loads again after a reconnect", async () => {
    await mountUsage();
    await flushAll();
    getMock.mockResolvedValue({ data: [usageWire("claude-code", 0.5)], response: { ok: true } });

    reconnect?.();
    await flushAll();

    expect(getMock).toHaveBeenCalledTimes(2);
  });

  it("has nothing for a harness that reports no limits", async () => {
    getMock.mockResolvedValue({ data: [], response: { ok: true } });
    const { result } = await mountUsage();
    await flushAll();

    expect(result.one.value).toBeNull();
  });
});
