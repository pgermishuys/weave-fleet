/**
 * Static export wrapper for the Session Detail page.
 *
 * Imports via _dynamic.tsx (a "use client" dynamic loader) so that
 * webpack's RSC analysis sees this file as a pure server module and
 * correctly detects generateStaticParams().
 * The Go server serves index.html as a fallback for all /sessions/* paths
 * and React handles routing client-side.
 */
import { Suspense } from "react";
import SessionDetailPageDynamic from "./_dynamic";

// Return a single placeholder entry so Next.js generates RSC payloads (tree +
// page .txt files) for this dynamic route during static export.  The actual
// session ID doesn't matter — the page is 100% client-rendered (ssr: false)
// and reads the real ID from useParams() at runtime.
//
// The Go server reuses these template payloads for ALL session IDs, which is
// correct because the RSC content is identical regardless of ID.
export function generateStaticParams() {
  return [{ id: "_" }];
}

export default function SessionDetailPage() {
  return (
    <Suspense>
      <SessionDetailPageDynamic />
    </Suspense>
  );
}
