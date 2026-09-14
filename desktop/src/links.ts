/**
 * Where a link goes. Fleet's own pages load in the window; web and mail links open in the system browser (GitHub
 * links, terminal URLs, previews "open in new tab"); anything else is blocked.
 */
export type LinkTarget = "window" | "browser" | "block";

export function classifyLink(target: string, fleetOrigin: string | null): LinkTarget {
  let url: URL;
  try {
    url = new URL(target);
  } catch {
    return "block";
  }
  if (fleetOrigin && url.origin === fleetOrigin) return "window";
  if (url.protocol === "http:" || url.protocol === "https:" || url.protocol === "mailto:") return "browser";
  return "block";
}
