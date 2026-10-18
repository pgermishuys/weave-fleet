import { readPageChoice, type PageChoice } from "@/lib/page-choice";

/**
 * Canvases the server stores for a session. The agent shows them through Fleet's
 * canvas tools; the user can close them. A kind this client doesn't know (one an
 * older server or a stale cache still sends) is ignored.
 *
 * `GET /api/sessions/{id}/canvases` items and `canvas.updated` properties share
 * these fields, so both go through the same mapping.
 */
export interface ServerCanvasSnapshot {
  canvasId: string;
  kind: string;
  title: string;
  version: number;
  state: unknown;
}

export type ServerCanvasKind = "browser" | "page";

/** A browser canvas: a page on this machine, and the app Fleet runs for it, if any. */
export interface BrowserPage {
  url: string;
  appId?: string;
}

/**
 * A page canvas: an HTML file the agent wrote, which Fleet copied and serves at `/pages/{pageId}/{entry}`.
 * `shownAt` changes every time the agent shows it, so the tab reloads.
 */
export interface ShownPage {
  pageId: string;
  entry: string;
  source: string;
  files: number;
  bytes: number;
  shownAt: string;
  warnings: string[];
  /** Shown in the tab's bar instead of the source, for a page Fleet wrote itself, e.g. "Walkthrough · compared with main at 55650b2". */
  label?: string;
  /** Set when the agent waits for a pick on the page (`waitForChoice`), and then says how it went. */
  choice?: PageChoice;
}

export function isServerCanvasKind(kind: string): kind is ServerCanvasKind {
  return kind === "browser" || kind === "page";
}

export function shownPage(state: unknown): ShownPage {
  const record = asRecord(state);
  const text = (value: unknown) => (typeof value === "string" ? value : "");
  const count = (value: unknown) => (isFiniteNumber(value) ? value : 0);
  return {
    pageId: text(record.pageId),
    entry: text(record.entry),
    source: text(record.source),
    files: count(record.files),
    bytes: count(record.bytes),
    shownAt: text(record.shownAt),
    warnings: asArray(record.warnings).filter((warning): warning is string => typeof warning === "string"),
    label: typeof record.label === "string" && record.label ? record.label : undefined,
    choice: readPageChoice(record.choice),
  };
}

/** Where Fleet serves a page canvas's page. */
export function pageAddress(page: Pick<ShownPage, "pageId" | "entry">): string {
  return `/pages/${encodeURIComponent(page.pageId)}/${page.entry.split("/").map(encodeURIComponent).join("/")}`;
}

export function browserPage(state: unknown): BrowserPage {
  const record = asRecord(state);
  const page: BrowserPage = { url: typeof record.url === "string" ? record.url : "" };
  if (typeof record.appId === "string" && record.appId) page.appId = record.appId;
  return page;
}

function asRecord(value: unknown): Record<string, unknown> {
  return value !== null && typeof value === "object" && !Array.isArray(value) ? (value as Record<string, unknown>) : {};
}

function asArray(value: unknown): unknown[] {
  return Array.isArray(value) ? value : [];
}

function isFiniteNumber(value: unknown): value is number {
  return typeof value === "number" && Number.isFinite(value);
}
