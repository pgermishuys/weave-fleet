/**
 * The canvas side of Fleet's preview bridge, protocol v1. The gateway adds a script to every previewed page
 * (`src/WeaveFleet.Api/Browser/preview-bridge.js`); it and the canvas talk with postMessage, because the page is
 * another origin. Types and versions either side doesn't know are ignored, so later types can be added.
 */

export const BRIDGE_VERSION = 1;

/** The page's hot-reload client, as the bridge found it. */
export type PreviewHmr = "vite" | "next" | "bun" | "webpack" | "dotnet-watch" | "none";

export type BridgeMessage =
  /** Once per page load. */
  | { type: "hello"; hmr: PreviewHmr; href: string; title: string }
  /** After every navigation. */
  | { type: "location"; href: string; title: string }
  /** The page's hot reload applied a change without reloading. */
  | { type: "update" };

export type NavAction = "back" | "forward" | "reload";

const HMR_KINDS = new Set<string>(["vite", "next", "bun", "webpack", "dotnet-watch", "none"]);

/** The bridge message in `data`, or null for anything else (another version, another sender, a malformed one). */
export function readBridgeMessage(data: unknown): BridgeMessage | null {
  if (data === null || typeof data !== "object") return null;
  const message = data as Record<string, unknown>;
  if (message.fleet !== BRIDGE_VERSION) return null;

  const href = typeof message.href === "string" ? message.href : null;
  const title = typeof message.title === "string" ? message.title : "";
  switch (message.type) {
    case "hello":
      if (href === null) return null;
      return {
        type: "hello",
        hmr: typeof message.hmr === "string" && HMR_KINDS.has(message.hmr) ? (message.hmr as PreviewHmr) : "none",
        href,
        title,
      };
    case "location":
      return href === null ? null : { type: "location", href, title };
    case "update":
      return { type: "update" };
    default:
      return null;
  }
}

export function navMessage(action: NavAction): { fleet: number; type: "nav"; action: NavAction } {
  return { fleet: BRIDGE_VERSION, type: "nav", action };
}

/**
 * The app's own address for a page the preview shows: the preview's origin swapped for the app's, so the address
 * bar reads `http://localhost:5173/cart` rather than the preview's port.
 */
export function appAddress(previewHref: string, previewOrigin: string, appOrigin: string): string {
  return previewHref.startsWith(previewOrigin) ? appOrigin + previewHref.slice(previewOrigin.length) : previewHref;
}
