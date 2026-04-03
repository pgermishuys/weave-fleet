/**
 * Static export wrapper for the GitHub Repository page.
 *
 * Imports via _dynamic.tsx (a "use client" dynamic loader) so that
 * webpack's RSC analysis sees this file as a pure server module and
 * correctly detects generateStaticParams().
 * The Go server serves index.html as a fallback for all /github/* paths
 * and React handles routing client-side.
 */
import { Suspense } from "react";
import GitHubRepoPageDynamic from "./_dynamic";

export function generateStaticParams() {
  return [{ owner: "_", repo: "_" }];
}

export default function GitHubRepoPage() {
  return (
    <Suspense>
      <GitHubRepoPageDynamic />
    </Suspense>
  );
}
