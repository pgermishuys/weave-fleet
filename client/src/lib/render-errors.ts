import { diagLog } from "@/lib/message-diagnostics";

/**
 * Says that a part of the page stopped rendering because it threw, and an error boundary drew its fallback instead.
 * A boundary swallows the error, so without this nothing sees it: not the browser's console, not the diagnostics log,
 * not the UI crawl. `where` is the address the person was on.
 */
export function reportRenderError(error: unknown, where: string): void {
  const message = error instanceof Error ? error.message : String(error);
  console.error(`[fleet] ${where} stopped rendering: ${message}`, error);
  diagLog("render-error", message, {
    where,
    ...(error instanceof Error && error.stack ? { stack: error.stack } : {}),
  });
}
