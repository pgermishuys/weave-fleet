import { beforeEach, describe, expect, it } from "vitest";
import { createPinia, setActivePinia } from "pinia";
import type { TerminalSummary } from "@/lib/terminal-api";
import { DEFAULT_DRAWER_HEIGHT, MIN_DRAWER_HEIGHT, useTerminalsStore } from "@/stores/terminals";

function terminal(id: string, title = "zsh"): TerminalSummary {
  return { id, title, status: "running", createdAt: "2026-09-13T10:00:00Z" };
}

describe("useTerminalsStore", () => {
  beforeEach(() => {
    setActivePinia(createPinia());
    globalThis.localStorage?.clear();
  });

  it("keeps the active tab when the list is reloaded, and falls back to the first", () => {
    const store = useTerminalsStore();
    store.setTerminals("s1", [terminal("t1"), terminal("t2", "zsh 2")]);
    store.setActive("s1", "t2");

    store.setTerminals("s1", [terminal("t1"), terminal("t2", "zsh 2"), terminal("t3", "zsh 3")]);
    expect(store.activeFor("s1")?.id).toBe("t2");

    store.setTerminals("s1", [terminal("t3", "zsh 3")]);
    expect(store.activeFor("s1")?.id).toBe("t3");
  });

  it("shows a tab you opened, but not one another window opened", () => {
    const store = useTerminalsStore();
    store.add("s1", terminal("t1"), true);

    store.applyEvent({ type: "terminal.opened", payload: { sessionId: "s1", terminalId: "t2", title: "zsh 2" } });
    expect(store.terminalsFor("s1").map((t) => t.id)).toEqual(["t1", "t2"]);
    expect(store.activeFor("s1")?.id).toBe("t1");

    store.add("s1", terminal("t3", "zsh 3"), true);
    expect(store.activeFor("s1")?.id).toBe("t3");
  });

  it("ignores an opened event for a tab it already has", () => {
    const store = useTerminalsStore();
    store.add("s1", terminal("t1", "bash"), true);

    store.applyEvent({ type: "terminal.opened", payload: { sessionId: "s1", terminalId: "t1", title: "zsh" } });

    expect(store.terminalsFor("s1")).toEqual([terminal("t1", "bash")]);
  });

  it("shows the neighbouring tab when the active one closes", () => {
    const store = useTerminalsStore();
    store.setTerminals("s1", [terminal("t1"), terminal("t2", "zsh 2"), terminal("t3", "zsh 3")]);
    store.setActive("s1", "t2");

    store.applyEvent({ type: "terminal.closed", payload: { sessionId: "s1", terminalId: "t2", title: "zsh 2", exitCode: 0 } });
    expect(store.activeFor("s1")?.id).toBe("t3");

    store.remove("s1", "t3");
    expect(store.activeFor("s1")?.id).toBe("t1");

    store.remove("s1", "t1");
    expect(store.activeFor("s1")).toBeNull();
  });

  it("keeps each session's tabs and drawer apart", () => {
    const store = useTerminalsStore();
    store.add("s1", terminal("t1"), true);
    store.setOpen("s1", true);

    expect(store.terminalsFor("s2")).toEqual([]);
    expect(store.isOpen("s1")).toBe(true);
    expect(store.isOpen("s2")).toBe(false);

    store.toggleOpen("s1");
    expect(store.isOpen("s1")).toBe(false);
  });

  it("never lets the drawer get shorter than the minimum", () => {
    const store = useTerminalsStore();
    expect(store.height).toBe(DEFAULT_DRAWER_HEIGHT);

    store.setHeight(40);
    expect(store.height).toBe(MIN_DRAWER_HEIGHT);

    store.setHeight(333.4);
    expect(store.height).toBe(333);
  });

  it("forgets a session's tabs and closes its drawer", () => {
    const store = useTerminalsStore();
    store.add("s1", terminal("t1"), true);
    store.setOpen("s1", true);

    store.forgetSession("s1");

    expect(store.terminalsFor("s1")).toEqual([]);
    expect(store.isOpen("s1")).toBe(false);
  });
});
