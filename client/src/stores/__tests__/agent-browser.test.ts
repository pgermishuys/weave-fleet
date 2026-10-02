import { beforeEach, describe, expect, it } from "vitest";
import { createPinia, setActivePinia } from "pinia";
import type { BrowserStep } from "@/lib/domain-events";
import { sameApp, useAgentBrowserStore } from "@/stores/agent-browser";

const TAB = "tab_00000000-0000-0000-0000-000000000001";

function step(seq: number, overrides: Partial<BrowserStep> = {}): BrowserStep {
  return {
    sessionId: "s1",
    seq,
    at: "2026-10-02T10:00:00Z",
    kind: "click",
    summary: `Step ${seq}`,
    detail: "click @e2 · 170 ms",
    ok: true,
    callId: "call_1",
    tabId: TAB,
    url: "http://127.0.0.1:5173/",
    title: "Signup demo",
    ...overrides,
  };
}

describe("agent browser store", () => {
  beforeEach(() => setActivePinia(createPinia()));

  it("lists a call's steps in order, once each", () => {
    const store = useAgentBrowserStore();
    store.applyStep(step(1, { kind: "tabs.open" }));
    store.applyStep(step(2));
    store.applyStep(step(2));
    store.applyStep(step(3, { callId: "call_2" }));

    expect(store.stepsOf("s1", "call_1").map((s) => s.seq)).toEqual([1, 2]);
    expect(store.stepsOf("s1", "call_2").map((s) => s.seq)).toEqual([3]);
    expect(store.stepsOf("s1", undefined)).toEqual([]);
  });

  it("keeps the agent's tabs from its steps: opened, moved and closed", () => {
    const store = useAgentBrowserStore();
    store.applyStep(step(1, { kind: "tabs.open" }));
    expect(store.of("s1").tabs.map((tab) => tab.url)).toEqual(["http://127.0.0.1:5173/"]);
    expect(store.of("s1").focusedTabId).toBe(TAB);

    store.applyStep(step(2, { kind: "navigate", url: "http://127.0.0.1:5173/settings", title: "Settings" }));
    expect(store.of("s1").tabs[0]).toMatchObject({ url: "http://127.0.0.1:5173/settings", title: "Settings" });

    store.applyStep(step(3, { kind: "tabs.close" }));
    expect(store.of("s1").tabs).toEqual([]);
    expect(store.of("s1").focusedTabId).toBeNull();
  });

  it("a failed step doesn't move the tab", () => {
    const store = useAgentBrowserStore();
    store.applyStep(step(1, { kind: "tabs.open" }));
    store.applyStep(step(2, { kind: "navigate", ok: false, url: "https://example.com/", error: "Fleet's browser settings only let agents open this session's own pages." }));

    expect(store.of("s1").tabs[0]!.url).toBe("http://127.0.0.1:5173/");
  });

  it("finds the agent's tab for the page a canvas shows, whatever loopback name either uses", () => {
    const store = useAgentBrowserStore();
    store.applyStep(step(1, { kind: "tabs.open" }));

    expect(store.tabFor("s1", "http://localhost:5173/")?.id).toBe(TAB);
    expect(store.tabFor("s1", "http://localhost:3000/")).toBeNull();
    expect(store.lastStepOn("s1", TAB)?.seq).toBe(1);
  });

  it("keeps steps that arrived while the session was loading", () => {
    const store = useAgentBrowserStore();
    store.applyStep(step(3));
    store.setFromServer("s1", { tabs: [], focusedTabId: null, steps: [step(1), step(2)] });

    expect(store.of("s1").steps.map((s) => s.seq)).toEqual([1, 2, 3]);
  });

  it("same app means same scheme, port and machine", () => {
    expect(sameApp("http://127.0.0.1:5173/a", "http://localhost:5173/b")).toBe(true);
    expect(sameApp("http://localhost:5173/", "https://localhost:5173/")).toBe(false);
    expect(sameApp("http://localhost:5173/", "http://localhost:5174/")).toBe(false);
    expect(sameApp("not a url", "http://localhost:5173/")).toBe(false);
  });
});
