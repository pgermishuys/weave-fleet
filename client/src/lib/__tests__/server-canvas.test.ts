import { describe, expect, it } from "vitest";
import { isServerCanvasKind, pageAddress, shownPage } from "@/lib/server-canvas";

describe("shownPage", () => {
  it("reads a page canvas's state and fills what's missing", () => {
    expect(shownPage({ pageId: "pg_1", entry: "a.html", source: "/tmp/a.html", files: 2, bytes: 10, shownAt: "t", warnings: ["w", 3] }))
      .toEqual({ pageId: "pg_1", entry: "a.html", source: "/tmp/a.html", files: 2, bytes: 10, shownAt: "t", warnings: ["w"] });
    expect(shownPage(null)).toEqual({ pageId: "", entry: "", source: "", files: 0, bytes: 0, shownAt: "", warnings: [] });
  });

  it("reads the choice a page waits on, and drops a malformed one", () => {
    const choice = { callId: "call_1", status: "answered", value: "a", summary: "Layout A", at: "2026-10-16T12:41:00Z" };
    expect(shownPage({ choice }).choice).toEqual({ ...choice, reason: undefined });
    expect(shownPage({ choice: { ...choice, status: "maybe" } }).choice).toBeUndefined();
    expect(shownPage({ choice: { status: "waiting" } }).choice).toBeUndefined();
  });

  it("addresses the page on Fleet, escaping each part of the file name", () => {
    expect(pageAddress({ pageId: "pg_1", entry: "options a.html" })).toBe("/pages/pg_1/options%20a.html");
    expect(pageAddress({ pageId: "pg_1", entry: "sub/b#c.html" })).toBe("/pages/pg_1/sub/b%23c.html");
  });
});

describe("isServerCanvasKind", () => {
  it("knows browser and page canvases, and not the retired diagram and sequence kinds", () => {
    expect(isServerCanvasKind("browser")).toBe(true);
    expect(isServerCanvasKind("page")).toBe(true);
    expect(isServerCanvasKind("diagram")).toBe(false);
    expect(isServerCanvasKind("sequence")).toBe(false);
    expect(isServerCanvasKind("terminal")).toBe(false);
  });
});
