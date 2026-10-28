// The pages agents show (fleet_page_show), on the phone: which pages a session has, the theme and the bridge a page
// gets in a full-screen WebView. No React Native in here. The web client's own rules are reused: page-bridge.ts reads
// the page's messages, page-theme-core.ts names the theme, server-canvas.ts reads a page canvas's state.
import type { CanvasEvent } from "@fleet/lib/domain-events";
import { readPageMessage, type PageMessage } from "@fleet/lib/page-bridge";
import { isChoiceLive, type PageChoice } from "@fleet/lib/page-choice";
import { CHART_SERIES, pageFrameName, pageThemeMessage, type PageTheme } from "@fleet/lib/page-theme-core";
import { pageAddress, shownPage, type ServerCanvasSnapshot, type ShownPage } from "@fleet/lib/server-canvas";
import type { AccumulatedMessage } from "@fleet/lib/client-types";
import { tokens } from "~/theme/tokens.generated";

export type { PageMessage };

/** One page of a session: a page canvas (shown as a tab on the desktop), or a page shown in the conversation. */
export interface SessionPage {
  /** The canvas id, or `call:{callId}` for a page in the conversation, which has no canvas. */
  key: string;
  title: string;
  pageId: string;
  entry: string;
  /** Changes each time the agent shows the page again, so an open page reloads. */
  shownAt: string;
  place: "tab" | "conversation";
  choice?: PageChoice;
}

// The app's tokens for each page variable, as page-theme-core.ts FROM_APP maps the desktop's CSS variables.
type TokenKey = keyof typeof tokens.dark;
export const PAGE_TOKENS: Readonly<Record<string, TokenKey>> = {
  "--fleet-bg": "panel",
  "--fleet-surface": "card",
  "--fleet-text": "text",
  "--fleet-muted": "muted",
  "--fleet-border": "border",
  "--fleet-accent": "accent",
  "--fleet-accent-surface": "accentDim",
  "--fleet-accent-text": "onAccent",
  "--fleet-success": "running",
  "--fleet-warning": "waiting",
  "--fleet-danger": "error",
  "--fleet-info": "complete",
};

// Not colours: the same values the desktop sends (main.css). The WebView has no Inter of its own, so the stack falls
// back to the phone's sans unless the page brings its fonts.
const FIXED: Readonly<Record<string, string>> = {
  "--fleet-radius": `${tokens.radius.card}px`,
  "--fleet-font-size": "14px",
  "--fleet-font-sans": '"Inter Variable", "Inter", system-ui, -apple-system, sans-serif',
  "--fleet-font-mono": '"JetBrains Mono Variable", "JetBrains Mono", ui-monospace, monospace',
};

/** The theme a page gets on the phone, from the app's own tokens. */
export function pageTheme(scheme: "light" | "dark"): PageTheme {
  const variables: Record<string, string> = {};
  for (const [name, token] of Object.entries(PAGE_TOKENS)) variables[name] = tokens[scheme][token];
  Object.assign(variables, FIXED);
  CHART_SERIES[scheme].forEach((color, index) => {
    variables[`--fleet-chart-${index + 1}`] = color;
  });
  return { appearance: scheme, variables };
}

const PAGE_TYPES = ["fleet:page-reply", "fleet:page-state", "fleet:page-hello", "fleet:page-choice"];

/**
 * Runs in the page before its content (as far as Android lets it). A page posts to `parent`; in a top-level WebView
 * that's the page itself, so the shim hears it and forwards it to the app. The theme goes in twice: in `window.name`
 * for Fleet's script at the top of the page, if the shim runs first, and as a `fleet:theme` message, which that script
 * hears if it ran first. `window.__fleetHost` lets the app post to the page, and keeps the shim from forwarding what
 * the app sent back to the app.
 */
export function pageShim(theme: PageTheme): string {
  const types = JSON.stringify(PAGE_TYPES);
  return `(() => {
  if (window.__fleetHost) return;
  try { window.name = ${JSON.stringify(pageFrameName("tab", theme))}; } catch (e) {}
  let fromHost = 0;
  window.__fleetHost = (message) => {
    if (message.type === "fleet:page-state") fromHost++;
    window.postMessage(message, "*");
  };
  addEventListener("message", (event) => {
    const data = event.data;
    if (event.source !== window || !data || ${types}.indexOf(data.type) < 0) return;
    if (fromHost > 0 && data.type === "fleet:page-state") { fromHost--; return; }
    try { window.ReactNativeWebView.postMessage(JSON.stringify(data)); } catch (e) {}
  });
  window.postMessage(${JSON.stringify(pageThemeMessage(theme))}, "*");
})(); true;`;
}

/** A script that posts `message` to the page, for WebView.injectJavaScript. */
export function postToPage(message: { type: string }): string {
  return `window.__fleetHost && window.__fleetHost(${JSON.stringify(message)}); true;`;
}

/** What the WebView's onMessage carried, if it's a page message the app acts on. Checked as the desktop checks it. */
export function readBridgeMessage(raw: string): PageMessage | null {
  try {
    return readPageMessage(JSON.parse(raw));
  } catch {
    return null;
  }
}

/** Where the machine serves the page. */
export function pageUrl(baseUrl: string, page: Pick<SessionPage, "pageId" | "entry">): string {
  return baseUrl.replace(/\/+$/, "") + pageAddress(page);
}

function fromCanvas(canvas: ServerCanvasSnapshot): SessionPage | null {
  if (canvas.kind !== "page") return null;
  const page: ShownPage = shownPage(canvas.state);
  if (!page.pageId || !page.entry) return null;
  return { key: canvas.canvasId, title: canvas.title, pageId: page.pageId, entry: page.entry, shownAt: page.shownAt, place: "tab", choice: page.choice };
}

/** The session's page canvases, from GET /api/sessions/{id}/canvases (oldest first). */
export function pagesFromCanvases(canvases: readonly ServerCanvasSnapshot[]): SessionPage[] {
  return canvases.map(fromCanvas).filter((page): page is SessionPage => page !== null);
}

/** The page list after a canvas event on the session's topic. A page shown again moves to the end, as the newest. */
export function applyCanvasEvent(pages: readonly SessionPage[], event: CanvasEvent): SessionPage[] {
  if (event.type === "canvas.closed") return pages.filter((page) => page.key !== event.payload.canvasId);
  if (event.type !== "canvas.updated") return [...pages];
  const page = fromCanvas(event.payload);
  const rest = pages.filter((p) => p.key !== event.payload.canvasId);
  if (!page) return rest;
  const before = pages.find((p) => p.key === page.key);
  // A change that isn't a new showing (a pick settling) keeps the page where it is.
  if (before && before.shownAt === page.shownAt) return pages.map((p) => (p.key === page.key ? page : p));
  return [...rest, page];
}

/** The page whose pick the agent is waiting for, while the session is in its turn. */
export function waitingPage(pages: readonly SessionPage[], activityStatus: string | undefined): SessionPage | null {
  return pages.find((page) => isChoiceLive(page.choice, activityStatus)) ?? null;
}

function record(value: unknown): Record<string, unknown> | null {
  return value !== null && typeof value === "object" && !Array.isArray(value) ? (value as Record<string, unknown>) : null;
}

/** Pages shown in the conversation: a fleet_page_show call whose metadata names the copy (`page: {id, entry}`). */
export function conversationPages(messages: readonly AccumulatedMessage[]): SessionPage[] {
  const pages: SessionPage[] = [];
  for (const message of messages) {
    for (const part of message.parts) {
      if (part.type !== "tool") continue;
      const state = record(part.state);
      const page = record(record(state?.metadata)?.page);
      const id = typeof page?.id === "string" ? page.id : "";
      const entry = typeof page?.entry === "string" ? page.entry : "";
      if (!id || !entry) continue;
      const title = typeof record(state?.input)?.title === "string" ? String(record(state?.input)?.title) : "";
      pages.push({ key: `call:${part.callId}`, title: title || entry, pageId: id, entry, shownAt: new Date(message.createdAt ?? 0).toISOString(), place: "conversation" });
    }
  }
  return pages;
}

/** A reply about a page: an ordinary prompt that names it, so the agent knows which one without its HTML. */
export function pagePrompt(page: Pick<SessionPage, "title" | "pageId">, text: string): string {
  return `About the page "${page.title}" (page ${page.pageId}):\n\n${text.trim()}`;
}
