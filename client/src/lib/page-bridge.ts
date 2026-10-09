/**
 * What a page tab (`fleet_page_show`) can say to Fleet. The page runs sandboxed with an opaque origin and no storage,
 * so it can't reach Fleet's API; postMessage to the canvas is its one way out, and the canvas reads only these
 * messages, only from its own frame. The fleet-plan skill's runtime uses all three
 * (`opencode/built-in-skills/fleet-plan/runtime/htmlplan.js`); any page may.
 */

export type PageMessage =
  /** Text for the session's composer, e.g. the answers to a plan. Fleet never sends it: the user does. */
  | { type: "fleet:page-reply"; text: string }
  /** The page's own state, kept by Fleet for the page and given back on the next hello. */
  | { type: "fleet:page-state"; state: unknown }
  /** The page has loaded. Fleet answers with `fleet:page-state` holding what it kept, or null. */
  | { type: "fleet:page-hello" };

/** Fleet's answer to a hello. */
export interface PageStateMessage {
  type: "fleet:page-state";
  state: unknown;
}

export const MAX_REPLY_LENGTH = 64 * 1024;
export const MAX_STATE_LENGTH = 256 * 1024;
const MAX_KEPT_PAGES = 50;

/** The page message in `data`, or null for anything else (an unknown type, a malformed or oversized one). */
export function readPageMessage(data: unknown): PageMessage | null {
  if (data === null || typeof data !== "object") return null;
  const message = data as Record<string, unknown>;

  switch (message.type) {
    case "fleet:page-reply":
      return typeof message.text === "string" && message.text.trim() && message.text.length <= MAX_REPLY_LENGTH
        ? { type: "fleet:page-reply", text: message.text }
        : null;
    case "fleet:page-state":
      return isSmallJson(message.state) ? { type: "fleet:page-state", state: message.state } : null;
    case "fleet:page-hello":
      return { type: "fleet:page-hello" };
    default:
      return null;
  }
}

function isSmallJson(value: unknown): boolean {
  try {
    const json = JSON.stringify(value);
    return json !== undefined && json.length <= MAX_STATE_LENGTH;
  } catch {
    return false;
  }
}

// Kept for as long as Fleet is open, so a page shown again (or reloaded) gets its answers back. A page shown again keeps
// its page id; the oldest pages are dropped first.
const keptStates = new Map<string, unknown>();

export function keepPageState(pageId: string, state: unknown): void {
  keptStates.delete(pageId);
  keptStates.set(pageId, state);
  while (keptStates.size > MAX_KEPT_PAGES) {
    keptStates.delete(keptStates.keys().next().value!);
  }
}

export function keptPageState(pageId: string): unknown {
  return keptStates.get(pageId) ?? null;
}

export function pageStateMessage(pageId: string): PageStateMessage {
  return { type: "fleet:page-state", state: keptPageState(pageId) };
}

/** For tests. */
export function forgetPageStates(): void {
  keptStates.clear();
}

/** The tallest a page in the conversation grows; past it, the page scrolls inside its frame. */
export const MAX_PAGE_HEIGHT = 2000;

/**
 * The height in a page's size report, or null for anything else. Fleet's script in every page sends it as MCP Apps
 * do: `{ jsonrpc: "2.0", method: "ui/notifications/size-changed", params: { height } }`.
 */
export function readPageSize(data: unknown): number | null {
  if (data === null || typeof data !== "object") return null;
  const message = data as Record<string, unknown>;
  if (message.jsonrpc !== "2.0" || message.method !== "ui/notifications/size-changed") return null;
  const params = message.params;
  const height = params !== null && typeof params === "object" ? (params as Record<string, unknown>).height : undefined;
  return typeof height === "number" && Number.isFinite(height) && height >= 0 ? Math.min(Math.ceil(height), MAX_PAGE_HEIGHT) : null;
}
