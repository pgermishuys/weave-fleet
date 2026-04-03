/**
 * Next.js instrumentation hook — runs once when the server starts.
 * https://nextjs.org/docs/app/building-your-application/optimizing/instrumentation
 */
export async function register() {
  if (process.env.NEXT_RUNTIME === "nodejs") {
    const { checkForUpdatesOnStartup } = await import("@/lib/server/version-check");
    checkForUpdatesOnStartup();
  }
}
