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
  | { type: "fleet:page-hello" }
  /** The page proposes a pick for a page that waits for one. Only Fleet's bar under the page sends it. */
  | { type: "fleet:page-choice"; value: string; summary: string }
  /**
   * Not something a page sends: a `fleet:page-choice` Fleet can't take, with why in plain words, so the bar under the
   * page can say so instead of waiting on a pick that never comes. `fleet.choose` gets nothing back, so the page and
   * the agent don't hear it; the user does, and can skip or write instead.
   */
  | { type: "fleet:page-choice-refused"; reason: string };

/** Fleet's answer to a hello. */
export interface PageStateMessage {
  type: "fleet:page-state";
  state: unknown;
}

export const MAX_REPLY_LENGTH = 64 * 1024;
/** Room for a page of many questions sending every answer at once, e.g. as JSON. The server takes as much. */
export const MAX_CHOICE_LENGTH = 16_000;
/** The longest summary the bar shows and the agent gets; a longer one is shortened, not refused. */
export const MAX_CHOICE_SUMMARY_LENGTH = 200;
export const MAX_STATE_LENGTH = 256 * 1024;
const MAX_KEPT_PAGES = 50;

/**
 * The page message in `data`, or null for anything else (an unknown type, a malformed or oversized one). A pick that
 * can't be taken is a `fleet:page-choice-refused` rather than null: the user is waiting on it.
 */
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
    case "fleet:page-choice": {
      const { value, summary } = message;
      if (value === undefined || value === null || (typeof value === "string" && !value.trim())) {
        return refused("The page's pick was empty, so there's nothing to send.");
      }
      if (typeof value !== "string" || (summary !== undefined && summary !== null && typeof summary !== "string")) {
        return refused("The page sent a pick Fleet can't read.");
      }
      if (value.length > MAX_CHOICE_LENGTH) {
        return refused(`The page's pick was too long to send: ${count(value.length)} characters, and the most is ${count(MAX_CHOICE_LENGTH)}.`);
      }
      return { type: "fleet:page-choice", value, summary: shortenSummary(summary?.trim() || value.trim()) };
    }
    default:
      return null;
  }
}

function refused(reason: string): PageMessage {
  return { type: "fleet:page-choice-refused", reason };
}

function count(n: number): string {
  return n.toLocaleString("en-US");
}

/** A page with many questions sums up every answer: past the limit, the summary ends in an ellipsis. */
function shortenSummary(summary: string): string {
  return summary.length <= MAX_CHOICE_SUMMARY_LENGTH ? summary : `${summary.slice(0, MAX_CHOICE_SUMMARY_LENGTH - 1)}…`;
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
