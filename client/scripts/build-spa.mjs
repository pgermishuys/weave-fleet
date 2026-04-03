#!/usr/bin/env node
/**
 * build-spa.mjs — Static SPA build for .NET binary embedding.
 *
 * Produces a static export in dist/ that the .NET API can serve as wwwroot.
 * No server-side API routes exist — the app is a pure stateless SPA.
 *
 * This script:
 *   1. Cleans stale dist/ and .next/ directories
 *   2. Runs `next build` with NEXT_BUILD_SPA=1 (enables `output: 'export'`)
 */

import { existsSync, rmSync } from "fs";
import { execSync } from "child_process";
import { fileURLToPath } from "url";
import { resolve, dirname } from "path";

const __dirname = dirname(fileURLToPath(import.meta.url));
const root = resolve(__dirname, "..");

// Resolve the build command — prefer npx
const buildCmd = (() => {
  try {
    execSync("npx --version", { stdio: "pipe" });
    return "npx next build";
  } catch {
    return "bunx next build";
  }
})();

// Step 1: Clean stale dist/ and .next/ to avoid leftover artifacts
for (const dir of ["dist", ".next"]) {
  const p = resolve(root, dir);
  if (existsSync(p)) {
    rmSync(p, { recursive: true, force: true });
    console.log(`Cleaned ${dir}/`);
  }
}

// Step 2: Build (static export)
let exitCode = 0;
try {
  execSync(buildCmd, {
    stdio: "inherit",
    cwd: root,
    env: { ...process.env, NEXT_BUILD_SPA: "1" },
  });
} catch (err) {
  exitCode = err.status ?? 1;
}

process.exit(exitCode);
