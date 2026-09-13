import { beforeEach, describe, expect, it, vi } from "vitest";
import { createPinia, setActivePinia } from "pinia";
import { shallowRef } from "vue";
import type { DomainEvent } from "@/lib/domain-events";
import type { TerminalSummary } from "@/lib/terminal-api";
import { useAppShellStore } from "@/stores/app-shell";
import { useTerminalsStore } from "@/stores/terminals";
import { flushAll, mountComposable } from "./test-utils";

const { apiFetchMock, subscribeV2Mock, reconnectCallbacks } = vi.hoisted(() => ({
  apiFetchMock: vi.fn(),
  subscribeV2Mock: vi.fn(),
  reconnectCallbacks: [] as Array<() => void>,
}));

vi.mock("@/lib/api-client", () => ({ apiFetch: apiFetchMock, wsUrl: (path: string) => path }));

vi.mock("@/composables/use-weave-socket", () => ({
  useWeaveSocket: () => ({ subscribeV2: subscribeV2Mock }),
  onReconnect: (callback: () => void) => {
    reconnectCallbacks.push(callback);
    return () => reconnectCallbacks.splice(reconnectCallbacks.indexOf(callback), 1);
  },
}));

let onEvent: ((event: DomainEvent) => void) | null = null;

function terminal(id: string, title = "zsh"): TerminalSummary {
  return { id, title, status: "running", createdAt: "2026-09-13T10:00:00Z" };
}

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { "Content-Type": "application/json" } });
}

function enableTerminals(enabled: boolean): void {
  const appShell = useAppShellStore();
  appShell.setConfig({ ...appShell.config, terminalEnabled: enabled });
}

describe("useSessionTerminals", () => {
  beforeEach(() => {
    setActivePinia(createPinia());
    apiFetchMock.mockReset();
    subscribeV2Mock.mockReset();
    reconnectCallbacks.length = 0;
    onEvent = null;
    subscribeV2Mock.mockImplementation((_topic: string, _onSnapshot: unknown, callback: (event: DomainEvent) => void) => {
      onEvent = callback;
      return () => {
        onEvent = null;
      };
    });
  });

  it("loads the session's terminals and applies its terminal events", async () => {
    enableTerminals(true);
    apiFetchMock.mockResolvedValue(jsonResponse([terminal("t1")]));
    const { useSessionTerminals } = await import("@/composables/use-session-terminals");

    const { wrapper } = await mountComposable(() => useSessionTerminals(shallowRef("s1")));
    await flushAll();

    const store = useTerminalsStore();
    expect(apiFetchMock).toHaveBeenCalledWith("/api/sessions/s1/terminals");
    expect(store.terminalsFor("s1").map((t) => t.id)).toEqual(["t1"]);

    onEvent?.({ type: "terminal.opened", payload: { sessionId: "s1", terminalId: "t2", title: "zsh 2" } });
    onEvent?.({ type: "terminal.opened", payload: { sessionId: "other", terminalId: "t9", title: "zsh" } });
    expect(store.terminalsFor("s1").map((t) => t.id)).toEqual(["t1", "t2"]);
    expect(store.terminalsFor("other")).toEqual([]);
    wrapper.unmount();
  });

  it("closes the drawer when the last terminal goes", async () => {
    enableTerminals(true);
    apiFetchMock.mockResolvedValue(jsonResponse([terminal("t1")]));
    const { useSessionTerminals } = await import("@/composables/use-session-terminals");
    const { wrapper } = await mountComposable(() => useSessionTerminals(shallowRef("s1")));
    await flushAll();
    const store = useTerminalsStore();
    store.setOpen("s1", true);

    onEvent?.({ type: "terminal.closed", payload: { sessionId: "s1", terminalId: "t1", title: "zsh", exitCode: 0 } });

    expect(store.isOpen("s1")).toBe(false);
    wrapper.unmount();
  });

  it("does nothing when Fleet has terminals turned off", async () => {
    enableTerminals(false);
    const { useSessionTerminals } = await import("@/composables/use-session-terminals");

    const { wrapper } = await mountComposable(() => useSessionTerminals(shallowRef("s1")));
    await flushAll();

    expect(apiFetchMock).not.toHaveBeenCalled();
    expect(subscribeV2Mock).not.toHaveBeenCalled();
    wrapper.unmount();
  });

  it("reloads after a reconnect", async () => {
    enableTerminals(true);
    apiFetchMock.mockResolvedValueOnce(jsonResponse([terminal("t1")]));
    apiFetchMock.mockResolvedValueOnce(jsonResponse([terminal("t1"), terminal("t2", "zsh 2")]));
    const { useSessionTerminals } = await import("@/composables/use-session-terminals");
    const { wrapper } = await mountComposable(() => useSessionTerminals(shallowRef("s1")));
    await flushAll();

    reconnectCallbacks.forEach((callback) => callback());
    await flushAll();

    expect(useTerminalsStore().terminalsFor("s1")).toHaveLength(2);
    wrapper.unmount();
  });

  it("opens a new terminal, shows it and opens the drawer", async () => {
    apiFetchMock.mockResolvedValue(jsonResponse(terminal("t7"), 201));
    const { openNewTerminal } = await import("@/composables/use-session-terminals");

    const error = await openNewTerminal("s1", 120, 30);

    const store = useTerminalsStore();
    expect(error).toBeNull();
    expect(apiFetchMock).toHaveBeenCalledWith("/api/sessions/s1/terminals", expect.objectContaining({ method: "POST", body: '{"cols":120,"rows":30}' }));
    expect(store.activeFor("s1")?.id).toBe("t7");
    expect(store.isOpen("s1")).toBe(true);
  });

  it("returns the server's message when a terminal can't be opened", async () => {
    apiFetchMock.mockResolvedValue(jsonResponse({ error: "This session already has 8 terminals. Close one to open another." }, 409));
    const { openNewTerminal } = await import("@/composables/use-session-terminals");

    const error = await openNewTerminal("s1", 120, 30);

    expect(error).toBe("This session already has 8 terminals. Close one to open another.");
    expect(useTerminalsStore().terminalsFor("s1")).toEqual([]);
  });

  it("closes a tab at once and puts it back if the server refuses", async () => {
    const store = useTerminalsStore();
    store.setTerminals("s1", [terminal("t1")]);
    store.setOpen("s1", true);
    apiFetchMock.mockResolvedValueOnce(jsonResponse({ error: "boom" }, 500));
    apiFetchMock.mockResolvedValueOnce(jsonResponse([terminal("t1")]));
    const { closeTerminalTab } = await import("@/composables/use-session-terminals");

    const closing = closeTerminalTab("s1", "t1");
    expect(store.terminalsFor("s1")).toEqual([]);
    expect(store.isOpen("s1")).toBe(false);
    await closing;

    expect(store.terminalsFor("s1").map((t) => t.id)).toEqual(["t1"]);
  });
});
