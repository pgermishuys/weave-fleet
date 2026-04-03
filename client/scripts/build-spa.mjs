#!/usr/bin/env node
/**
 * build-spa.mjs — Static SPA build for Go binary embedding.
 *
 * The Next.js API routes in src/app/api/ are Node.js server-side handlers
 * used in standalone mode. With `output: 'export'`, Next.js cannot include
 * dynamic API routes, so we hide them during build.
 *
 * Dynamic page routes (sessions/[id], github/[owner]/[repo], etc.) are NOT
 * hidden. They use generateStaticParams() with placeholder entries so
 * Next.js generates the RSC payloads required for client-side navigation.
 * The Go server serves these template payloads for any dynamic ID.
 *
 * This script:
 *   1. Temporarily renames src/app/api → src/app/_api (excluded by App Router)
 *   2. Runs `next build` (static export → dist/)
 *   3. Restores all hidden dirs
 *
 * If the build fails, all directories are always restored before exiting.
 */

import { renameSync, existsSync, rmSync } from "fs";
import { execSync } from "child_process";
import { fileURLToPath } from "url";
import { resolve, dirname } from "path";

const __dirname = dirname(fileURLToPath(import.meta.url));
const root = resolve(__dirname, "..");

// Resolve the build command — prefer npx, fall back to bunx for bun-only envs
const buildCmd = (() => {
  try {
    execSync("npx --version", { stdio: "pipe" });
    return "npx next build";
  } catch {
    return "bunx next build";
  }
})();

// Directories to hide during static build.
//
// API routes must be hidden — they are Node.js server-side handlers that
// cannot be statically exported.
//
// Dynamic page routes (sessions/[id], github/[owner]/[repo], etc.) MUST NOT
// be hidden. Next.js App Router requires pre-generated RSC payloads (.txt
// files) for client-side navigation to work. If a dynamic route is hidden
// during build, no RSC payloads are generated, and clicking a <Link> to that
// route will silently fail (URL changes but the page content doesn't update).
//
// Each dynamic route uses generateStaticParams() with a placeholder entry
// (e.g. [{ id: "_" }]) so Next.js generates a template set of RSC payloads.
// The Go server reuses these templates for any actual ID — this works because
// the pages are 100% client-rendered (ssr: false) and read params from
// useParams() at runtime.
const hidePairs = [
  [resolve(root, "src/app/api"),                     resolve(root, "src/app/_api")],
  // NOTE: dynamic page routes are intentionally NOT hidden — see comment above.
];

function restore() {
  for (const [original, hidden] of hidePairs) {
    if (existsSync(hidden)) {
      renameSync(hidden, original);
      console.log(`Restored ${original.replace(root + "/", "")}`);
    }
  }
}

// Ensure we restore on SIGINT/SIGTERM
process.on("SIGINT", () => { restore(); process.exit(1); });
process.on("SIGTERM", () => { restore(); process.exit(1); });

// Step 1: Clean stale dist/ and .next/ to avoid leftover type-check artifacts
for (const dir of ["dist", ".next"]) {
  const p = resolve(root, dir);
  if (existsSync(p)) {
    rmSync(p, { recursive: true, force: true });
    console.log(`Cleaned ${dir}/`);
  }
}

// Step 2: Hide all problematic dirs
for (const [original, hidden] of hidePairs) {
  if (existsSync(original)) {
    renameSync(original, hidden);
    console.log(`Temporarily moved ${original.replace(root + "/", "")} → ${hidden.replace(root + "/", "")}`);
  }
}

// Step 3: Build
let exitCode = 0;
try {
  execSync(buildCmd, { stdio: "inherit", cwd: root, env: { ...process.env, NEXT_BUILD_SPA: "1" } });
} catch (err) {
  exitCode = err.status ?? 1;
} finally {
  // Step 4: Restore
  restore();
}

process.exit(exitCode);
