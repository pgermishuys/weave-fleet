import { describe, expect, test } from "bun:test";
import type { CanvasEvent } from "@fleet/lib/domain-events";
import { FROM_APP } from "@fleet/lib/page-theme-core";
import type { ServerCanvasSnapshot } from "@fleet/lib/server-canvas";
import type { AccumulatedMessage } from "@fleet/lib/client-types";
import { applyCanvasEvent, conversationPages, PAGE_TOKENS, pagePrompt, pageShim, pagesFromCanvases, pageTheme, pageUrl, postToPage, readBridgeMessage, waitingPage } from "./pages";
import { tokens } from "~/theme/tokens.generated";

const canvas = (canvasId: string, state: object, kind = "page", title = "Three ways to page the results"): ServerCanvasSnapshot =>
  ({ canvasId, kind, title, version: 1, state });
const page = (pageId: string, shownAt = "2026-10-24T10:31:00Z", choice?: object) => ({ pageId, entry: "index.html", source: "/x", files: 1, bytes: 10, shownAt, warnings: [], choice });
const updated = (c: ServerCanvasSnapshot) => ({ type: "canvas.updated", payload: { sessionId: "s", actor: "agent", ...c } }) as unknown as CanvasEvent;

describe("page theme", () => {
  test("has every variable the desktop sends, from the app's tokens", () => {
    for (const name of Object.keys(FROM_APP)) expect(pageTheme("dark").variables[name]).toBeDefined();
    expect(Object.keys(PAGE_TOKENS).every((name) => name in FROM_APP)).toBe(true);
    const light = pageTheme("light");
    expect(light.appearance).toBe("light");
    expect(light.variables["--fleet-bg"]).toBe(tokens.light.panel);
    expect(light.variables["--fleet-surface"]).toBe(tokens.light.card);
    expect(light.variables["--fleet-chart-1"]).toBe("#5B6EC7");
    expect(pageTheme("dark").variables["--fleet-accent"]).toBe(tokens.dark.accent);
  });
});

describe("page list", () => {
  test("keeps page canvases with a page, oldest first", () => {
    const pages = pagesFromCanvases([canvas("c1", page("p1")), canvas("b", { url: "http://x" }, "browser"), canvas("c2", { pageId: "" }), canvas("c3", page("p3"))]);
    expect(pages.map((p) => p.key)).toEqual(["c1", "c3"]);
    expect(pages[0]).toMatchObject({ pageId: "p1", entry: "index.html", place: "tab", title: "Three ways to page the results" });
  });

  test("a page shown again moves to the end; a pick settling keeps its place; closing removes it", () => {
    let pages = pagesFromCanvases([canvas("c1", page("p1")), canvas("c2", page("p2"))]);
    pages = applyCanvasEvent(pages, updated(canvas("c1", page("p1", "2026-10-24T10:40:00Z"))));
    expect(pages.map((p) => p.key)).toEqual(["c2", "c1"]);
    pages = applyCanvasEvent(pages, updated(canvas("c2", page("p2", "2026-10-24T10:31:00Z", { callId: "k", status: "answered", value: "paged", at: "t" }))));
    expect(pages.map((p) => p.key)).toEqual(["c2", "c1"]);
    expect(pages[0].choice?.status).toBe("answered");
    pages = applyCanvasEvent(pages, { type: "canvas.closed", payload: { sessionId: "s", canvasId: "c2" } } as CanvasEvent);
    expect(pages.map((p) => p.key)).toEqual(["c1"]);
    expect(applyCanvasEvent(pages, updated(canvas("c9", page("p9")))).map((p) => p.key)).toEqual(["c1", "c9"]);
  });

  test("a waiting pick counts only while the session is in its turn", () => {
    const pages = pagesFromCanvases([canvas("c1", page("p1")), canvas("c2", page("p2", "t", { callId: "k", status: "waiting", at: "t" }))]);
    expect(waitingPage(pages, "waiting_input")?.key).toBe("c2");
    expect(waitingPage(pages, "idle")).toBeNull();
  });

  test("pages shown in the conversation come from their calls' metadata", () => {
    const messages = [{
      messageId: "m", sessionId: "s", role: "assistant", createdAt: Date.parse("2026-10-24T10:29:00Z"),
      parts: [
        { partId: "1", type: "tool", tool: "fleet_page_show", callId: "k1", state: { status: "completed", input: { title: "Row count by month" }, metadata: { page: { id: "p7", entry: "chart.html" } } } },
        { partId: "2", type: "tool", tool: "bash", callId: "k2", state: { status: "completed", input: {} } },
      ],
    }] as unknown as AccumulatedMessage[];
    expect(conversationPages(messages)).toEqual([{ key: "call:k1", title: "Row count by month", pageId: "p7", entry: "chart.html", shownAt: "2026-10-24T10:29:00.000Z", place: "conversation" }]);
  });

  test("url and prompt", () => {
    expect(pageUrl("http://127.0.0.1:5431/", { pageId: "ab", entry: "sub dir/i.html" })).toBe("http://127.0.0.1:5431/pages/ab/sub%20dir/i.html");
    expect(pagePrompt({ title: "Export", pageId: "ab" }, " drop the 10k row \n")).toBe('About the page "Export" (page ab):\n\ndrop the 10k row');
  });
});

describe("bridge", () => {
  test("reads only page messages, checked as the desktop does", () => {
    expect(readBridgeMessage(JSON.stringify({ type: "fleet:page-choice", value: "paged", summary: "Paged table" }))).toEqual({ type: "fleet:page-choice", value: "paged", summary: "Paged table" });
    expect(readBridgeMessage(JSON.stringify({ type: "fleet:page-reply", text: "  " }))).toBeNull();
    expect(readBridgeMessage(JSON.stringify({ type: "fleet:theme" }))).toBeNull();
    expect(readBridgeMessage("not json")).toBeNull();
  });

  // The shim in a stand-in window: postMessage delivers to the window's own listeners, as in a top-level WebView.
  function fakeWindow() {
    const listeners: ((event: { source: unknown; data: unknown }) => void)[] = [];
    const sent: unknown[] = [];
    const received: unknown[] = [];
    const win: Record<string, unknown> = { name: "" };
    win.postMessage = (data: unknown) => {
      const copy = JSON.parse(JSON.stringify(data));
      received.push(copy);
      for (const listener of listeners) listener({ source: win, data: copy });
    };
    win.addEventListener = (_: string, listener: (event: { source: unknown; data: unknown }) => void) => listeners.push(listener);
    win.ReactNativeWebView = { postMessage: (text: string) => sent.push(JSON.parse(text)) };
    const run = (script: string) => new Function("window", "addEventListener", script)(win, win.addEventListener);
    return { win, sent, received, run };
  }

  test("names the window with the theme, posts fleet:theme, forwards the page's messages and not the app's", () => {
    const { win, sent, received, run } = fakeWindow();
    run(pageShim(pageTheme("light")));
    expect(String(win.name).startsWith("fleet:")).toBe(true);
    expect(JSON.parse(String(win.name).slice(6))).toMatchObject({ place: "tab", appearance: "light" });
    expect(received[0]).toMatchObject({ type: "fleet:theme", appearance: "light" });

    const post = win.postMessage as (data: unknown) => void;
    post({ type: "fleet:page-hello" });
    post({ type: "fleet:page-choice", value: "paged", summary: "Paged table" });
    post({ type: "fleet:other" });
    run(postToPage({ type: "fleet:page-state", state: { read: [1] } } as { type: string }));
    post({ type: "fleet:page-state", state: { read: [1, 2] } });
    expect(sent).toEqual([
      { type: "fleet:page-hello" },
      { type: "fleet:page-choice", value: "paged", summary: "Paged table" },
      { type: "fleet:page-state", state: { read: [1, 2] } },
    ]);
    run(pageShim(pageTheme("dark")));
    expect(JSON.parse(String(win.name).slice(6)).appearance).toBe("light");
  });
});
