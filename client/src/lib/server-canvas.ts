import type { VisualPayload } from "@/lib/visual-payload";

/**
 * Canvases the server stores for a session. The agent opens and changes them
 * through Fleet's canvas tools; the user sees them read-only and can close them.
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

export type ServerCanvasKind = "diagram" | "sequence" | "browser" | "page";

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
}

type FlowDirection = "TB" | "LR" | "BT" | "RL";

/** A diagram box as the flow renderer takes it. `x`/`y` are the box's top-left, set only once the user has placed it. */
export interface FlowBox {
  id: string;
  label: string;
  detail?: string;
  x?: number;
  y?: number;
}

export interface FlowLink {
  id: string;
  source: string;
  target: string;
  label?: string;
  style?: "dashed" | "planned";
}

// A type literal rather than an interface, so it fits VisualPayload's Record content.
export type DiagramFlowContent = {
  direction: FlowDirection;
  nodes: FlowBox[];
  edges: FlowLink[];
};

const DIRECTIONS = new Set<string>(["TB", "LR", "BT", "RL"]);

export function isServerCanvasKind(kind: string): kind is ServerCanvasKind {
  return kind === "diagram" || kind === "sequence" || kind === "browser" || kind === "page";
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

/** Maps a server canvas to the payload the visual renderers take, or null for a kind this client can't show. */
export function serverCanvasPayload(canvas: ServerCanvasSnapshot): VisualPayload | null {
  switch (canvas.kind) {
    case "diagram":
      return { $type: "visual/flow", title: canvas.title, content: diagramFlowContent(canvas.state) };
    case "sequence":
      return { $type: "visual/sequence", title: canvas.title, content: sequenceSource(canvas.state) };
    default:
      return null;
  }
}

/**
 * Diagram state → flow content: `from`/`to` become `source`/`target`, and
 * positions pass through only when the state has both. Boxes without one are
 * laid out by the renderer, which never writes its layout back.
 */
export function diagramFlowContent(state: unknown): DiagramFlowContent {
  const record = asRecord(state);
  const direction = typeof record.direction === "string" ? record.direction.toUpperCase() : "TB";

  const nodes = asArray(record.nodes).flatMap((raw): FlowBox[] => {
    const node = asRecord(raw);
    if (typeof node.id !== "string") return [];

    const box: FlowBox = { id: node.id, label: typeof node.label === "string" ? node.label : node.id };
    if (typeof node.detail === "string" && node.detail) box.detail = node.detail;
    if (isFiniteNumber(node.x) && isFiniteNumber(node.y)) {
      box.x = node.x;
      box.y = node.y;
    }
    return [box];
  });

  const nodeIds = new Set(nodes.map((node) => node.id));
  const edges = asArray(record.edges).flatMap((raw): FlowLink[] => {
    const edge = asRecord(raw);
    if (typeof edge.id !== "string" || typeof edge.from !== "string" || typeof edge.to !== "string") return [];
    if (!nodeIds.has(edge.from) || !nodeIds.has(edge.to)) return [];

    const link: FlowLink = { id: edge.id, source: edge.from, target: edge.to };
    if (typeof edge.label === "string" && edge.label) link.label = edge.label;
    if (edge.style === "dashed" || edge.style === "planned") link.style = edge.style;
    return [link];
  });

  return {
    direction: DIRECTIONS.has(direction) ? (direction as FlowDirection) : "TB",
    nodes,
    edges,
  };
}

function sequenceSource(state: unknown): string {
  const source = asRecord(state).source;
  return typeof source === "string" ? source : "";
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
