import { afterEach, describe, expect, it, vi } from "vitest";
import { effectScope, nextTick, watch } from "vue";
import {
  composerBands,
  isDraftView,
  modPaneViewId,
  modPanes,
  statusChips,
  toolRowViewId,
  toolRowViews,
  useModView,
  type ModTreeView,
} from "@/lib/mods/points";

const mods = [{ name: "m", draft: false }];
const tree = null;

describe("mod contribution points", () => {
  afterEach(() => {
    vi.restoreAllMocks();
    for (const point of [toolRowViews, composerBands, statusChips, modPanes]) point.clear();
  });

  it("builds tool row ids from site, session and call, and pane ids from session, mod and pane", () => {
    expect(toolRowViewId("ToolUse", "s1", "c1")).toBe("ToolUse:s1:c1");
    expect(modPaneViewId("s1", "ci", "p1")).toBe("s1:ci:p1");
  });

  it("keeps ToolUse and ToolResult of one call apart, and the same call id in two sessions", () => {
    const off = toolRowViews.contribute("m", [
      { site: "ToolUse", sessionId: "s1", tool: "bash", callId: "c1", tree, mods },
      { site: "ToolResult", sessionId: "s1", tool: "bash", callId: "c1", tree, mods },
      { site: "ToolUse", sessionId: "s2", tool: "bash", callId: "c1", tree, mods },
    ]);
    expect(toolRowViews.items.value).toHaveLength(3);
    expect(toolRowViews.get("ToolUse:s1:c1")?.site).toBe("ToolUse");
    expect(toolRowViews.get("ToolResult:s1:c1")?.site).toBe("ToolResult");
    expect(toolRowViews.get("ToolUse:s2:c1")?.sessionId).toBe("s2");
    off();
    expect(toolRowViews.items.value).toHaveLength(0);
  });

  it("contributes, replaces and removes a composer band per session", () => {
    composerBands.contribute("m", [{ sessionId: "s1", tree, mods }]);
    composerBands.contribute("m", [{ sessionId: "s2", tree, mods }]);
    expect(composerBands.items.value.map((v) => v.sessionId).sort()).toEqual(["s1", "s2"]);
    const next = { sessionId: "s1", tree: { type: "Fleet" } as const, mods };
    vi.spyOn(console, "warn").mockImplementation(() => {});
    composerBands.contribute("m2", [next]);
    expect(composerBands.get("s1")).toBe(next);
    expect(composerBands.items.value).toHaveLength(2);
    composerBands.removeByOwner("m");
    expect(composerBands.get("s2")).toBeUndefined();
    expect(composerBands.get("s1")).toBe(next);
  });

  it("contributes, replaces and removes a status chip per session", () => {
    const off = statusChips.contribute("m", [{ sessionId: "s1", tree, mods }]);
    expect(statusChips.get("s1")).toBeDefined();
    const replacement = { sessionId: "s1", tree, mods: [{ name: "n", draft: true }] };
    vi.spyOn(console, "warn").mockImplementation(() => {});
    statusChips.contribute("n", [replacement]);
    expect(statusChips.get("s1")).toBe(replacement);
    off();
    expect(statusChips.get("s1")).toBe(replacement);
    statusChips.removeByOwner("n");
    expect(statusChips.items.value).toHaveLength(0);
  });

  it("keeps the tool name as data: a row is found without it", () => {
    toolRowViews.contribute("m", [{ site: "ToolUse", sessionId: "s1", tool: "whatever", callId: "c1", tree, mods }]);
    expect(toolRowViews.get(toolRowViewId("ToolUse", "s1", "c1"))?.tool).toBe("whatever");
  });

  it("keys panes by session, mod and pane id and sorts by title", () => {
    modPanes.contribute("m", [
      { sessionId: "s1", paneId: "b", title: "Beta", mod: "m", tree, mods },
      { sessionId: "s1", paneId: "a", title: "Alpha", mod: "m", tree, mods },
      { sessionId: "s2", paneId: "a", title: "Other", mod: "m", tree, mods },
    ]);
    expect(modPanes.get("s1:m:a")?.title).toBe("Alpha");
    expect(modPanes.items.value.map((v) => v.title)).toEqual(["Alpha", "Beta", "Other"]);
    vi.spyOn(console, "warn").mockImplementation(() => {});
    modPanes.contribute("m", [{ sessionId: "s1", paneId: "a", title: "Renamed", mod: "m", tree, mods }]);
    expect(modPanes.items.value).toHaveLength(3);
    expect(modPanes.get("s1:m:a")?.title).toBe("Renamed");
    modPanes.removeByOwner("m");
    expect(modPanes.items.value).toHaveLength(0);
  });

  it("two mods can use the same pane id in one session", () => {
    modPanes.contribute("one", [{ sessionId: "s1", paneId: "runs", title: "One", mod: "one", tree, mods }]);
    modPanes.contribute("two", [{ sessionId: "s1", paneId: "runs", title: "Two", mod: "two", tree, mods }]);
    expect(modPanes.items.value.map((v) => v.title)).toEqual(["One", "Two"]);
    expect(modPanes.get(modPaneViewId("s1", "one", "runs"))?.title).toBe("One");
    expect(modPanes.get(modPaneViewId("s1", "two", "runs"))?.title).toBe("Two");
  });

  it("calls a view a draft when any author is a draft", () => {
    const view = (...drafts: boolean[]): Pick<ModTreeView, "mods"> => ({
      mods: drafts.map((draft, i) => ({ name: `m${i}`, draft })),
    });
    expect(isDraftView(view())).toBe(false);
    expect(isDraftView(view(false, false))).toBe(false);
    expect(isDraftView(view(false, true))).toBe(true);
    expect(isDraftView(view(true))).toBe(true);
  });

  it("useModView notifies only when its own id's view changes", async () => {
    const scope = effectScope();
    const seen: Array<string | undefined> = [];
    scope.run(() => {
      const one = useModView(composerBands, () => "s1");
      watch(one, (view) => seen.push(view?.mods[0]?.name), { flush: "sync" });
    });
    composerBands.contribute("a", [{ sessionId: "s1", tree, mods: [{ name: "first", draft: false }] }]);
    composerBands.contribute("b", [{ sessionId: "s2", tree, mods }]);
    composerBands.removeByOwner("b");
    await nextTick();
    expect(seen).toEqual(["first"]);
    scope.stop();
  });
});
