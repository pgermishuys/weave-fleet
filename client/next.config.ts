import type { NextConfig } from "next";
import { execSync } from "child_process";
import packageJson from "./package.json" with { type: "json" };

function getGitCommitSha(): string {
  try {
    return execSync("git rev-parse --short HEAD", { encoding: "utf-8" }).trim();
  } catch {
    return "unknown";
  }
}

function getAppVersion(): string {
  // CI sets APP_VERSION from the git tag (e.g. "v0.4.0" → "0.4.0")
  if (process.env.APP_VERSION) {
    return process.env.APP_VERSION.replace(/^v/, "");
  }
  // Fallback to package.json for local dev
  return packageJson.version;
}

// Static export is only used during SPA builds (bun run build / build:spa).
// During `next dev`, output must NOT be 'export' — it disables middleware,
// API routes, and other dev-server features.
// The SPA build script sets NEXT_BUILD_SPA=1 to trigger static export mode.
const isBuild = process.env.NEXT_BUILD_SPA === "1";

const nextConfig: NextConfig = {
  // Static export — produces dist/ with index.html + JS/CSS bundles.
  // Served by the .NET backend. No Node.js server required.
  ...(isBuild ? { output: 'export' as const } : {}),
  distDir: 'dist',
  compress: true,
  // Workaround for Next.js 16.1.x bug where generateBuildId crashes when
  // config.generateBuildId is undefined (it calls `generate()` without a
  // null check). Providing an explicit function avoids the TypeError.
  generateBuildId: () => getGitCommitSha(),
  // Disable image optimization (not supported with static export)
  images: { unoptimized: true },
  // Pin the workspace root to this directory to avoid Turbopack picking up
  // the src-tauri/app-bundle lockfile as the monorepo root.
  turbopack: {
    root: __dirname,
  },
  env: {
    NEXT_PUBLIC_APP_VERSION: getAppVersion(),
    NEXT_PUBLIC_COMMIT_SHA: getGitCommitSha(),
    // NEXT_PUBLIC_WEAVE_PROFILE — baked at build time as fallback; the
    // /api/profile endpoint provides the authoritative runtime value.
    NEXT_PUBLIC_WEAVE_PROFILE: process.env.WEAVE_PROFILE || "default",
    // NEXT_PUBLIC_API_BASE_URL — set at build time to point the frontend at
    // an external API server (e.g. "http://localhost:3000"). When unset or
    // empty, all fetch calls use relative URLs (same-origin / standalone mode).
    // See also: .env.development.split for split-mode dev setup.
  },
};

export default nextConfig;
