/**
 * Static export wrapper for the Repository Detail page.
 *
 * Imports via _dynamic.tsx (a "use client" dynamic loader) so that
 * webpack's RSC analysis sees this file as a pure server module and
 * correctly detects generateStaticParams().
 * The Go server serves index.html as a fallback for all /repositories/* paths
 * and React handles routing client-side.
 */
import { Suspense } from "react";
import RepositoryDetailPageDynamic from "./_dynamic";

export function generateStaticParams() {
  return [{ path: "_" }];
}

export default function RepositoryDetailPage() {
  return (
    <Suspense>
      <RepositoryDetailPageDynamic />
    </Suspense>
  );
}
