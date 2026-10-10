import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { onModTreeInvalid } from "@/lib/mods/failures";
import { NOTHING_DRAWN, resolveModView } from "@/lib/mods/resolve";
import type { ModTreeView } from "@/lib/mods/points";
import type { ModWireElement } from "@/lib/mods/types";
import * as validate from "@/lib/mods/validate";

const pill: ModWireElement = { type: "Pill", props: { tone: "good", label: "ok" } };
const input: ModWireElement = { type: "Input", props: { key: "q" }, handles: {} };

function view(tree: ModWireElement | null, mods = [{ name: "chips", draft: false }]): ModTreeView {
  return { tree, mods };
}

describe("resolveModView", () => {
  beforeEach(() => {
    vi.spyOn(console, "warn").mockImplementation(() => {});
  });

  afterEach(() => {
    vi.restoreAllMocks();
  });

  it("does not draw without a view or without mods", () => {
    expect(resolveModView(undefined, "Pane")).toEqual({ draws: false });
    expect(resolveModView(view(pill, []), "Pane")).toEqual({ draws: false });
  });

  it("answers \"nothing drawn\" with one shared frozen object", () => {
    expect(resolveModView(undefined, "Pane")).toBe(NOTHING_DRAWN);
    expect(resolveModView(view(pill, []), "ToolUse")).toBe(NOTHING_DRAWN);
    vi.spyOn(console, "warn").mockImplementation(() => {});
    expect(resolveModView(view({ type: "Input", props: { key: "z" }, handles: {} }), "ToolUse")).toBe(NOTHING_DRAWN);
    expect(Object.isFrozen(NOTHING_DRAWN)).toBe(true);
  });

  it("draws a valid tree", () => {
    const v = view(pill);
    expect(resolveModView(v, "Pane")).toEqual({ draws: true, tree: pill, draft: false, view: v });
  });

  it("draws a null tree as nothing", () => {
    expect(resolveModView(view(null), "ToolResult")).toMatchObject({ draws: true, tree: null });
  });

  it("marks a draft", () => {
    const v = view(pill, [{ name: "a", draft: false }, { name: "b", draft: true }]);
    expect(resolveModView(v, "Pane")).toMatchObject({ draws: true, draft: true });
  });

  it("falls back to Fleet's site for an invalid tree and reports it", () => {
    const seen: unknown[] = [];
    const off = onModTreeInvalid((failure) => seen.push(failure));
    const v = view(input, [{ name: "m", draft: true }]);
    expect(resolveModView(v, "ToolUse")).toEqual({ draws: false });
    off();
    expect(seen).toEqual([
      { site: "ToolUse", reason: "Input is not inline (ToolUse takes inline elements only)", mods: [{ name: "m", draft: true }] },
    ]);
  });

  it("draws the same tree at a site that allows it", () => {
    expect(resolveModView(view(input), "Pane")).toMatchObject({ draws: true });
  });

  it("reports an invalid tree once however often it is resolved", () => {
    const seen: unknown[] = [];
    const off = onModTreeInvalid((failure) => seen.push(failure));
    const v = view(input);
    for (let i = 0; i < 5; i++) resolveModView(v, "StatusChip");
    expect(seen).toHaveLength(1);
    // Same tree object at a different site is a different check.
    resolveModView(v, "Pane");
    expect(seen).toHaveLength(1);
    // A new view with a new tree reports again.
    resolveModView(view({ ...input }), "StatusChip");
    expect(seen).toHaveLength(2);
    off();
  });

  it("validates a tree once per site", () => {
    const spy = vi.spyOn(validate, "validateModTree");
    const tree = { ...pill };
    resolveModView(view(tree), "Pane");
    resolveModView(view(tree), "Pane");
    resolveModView(view(tree), "ToolUse");
    expect(spy).toHaveBeenCalledTimes(2);
  });

  it("warns in development", () => {
    resolveModView(view({ ...input }), "ToolUse");
    expect(console.warn).toHaveBeenCalledOnce();
    expect(vi.mocked(console.warn).mock.calls[0]![0]).toContain("is not inline");
  });

  it("returns the cut tree when text was cut", () => {
    const long: ModWireElement = { type: "Markdown", props: { text: "a".repeat(100_001) } };
    const result = resolveModView(view(long), "Pane");
    expect(result).toMatchObject({ draws: true });
    expect((result as unknown as { tree: { props: { text: string } } }).tree.props.text).toHaveLength(100_000);
  });
});
