import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";
import { resolve } from "path";
import { execSync } from "child_process";
import packageJson from "./package.json";

function getGitCommitSha(): string {
  try {
    return execSync("git rev-parse --short HEAD", { encoding: "utf-8" }).trim();
  } catch {
    return "unknown";
  }
}

function getAppVersion(): string {
  if (process.env.APP_VERSION) {
    return process.env.APP_VERSION.replace(/^v/, "");
  }
  return packageJson.version;
}

export default defineConfig({
  plugins: [react()],
  resolve: {
    alias: {
      "@": resolve(__dirname, "./src"),
    },
  },
  define: {
    // Replicate the NEXT_PUBLIC_* env vars as VITE_* equivalents
    // These are baked at build time, matching Next.js behavior
    "import.meta.env.VITE_APP_VERSION": JSON.stringify(getAppVersion()),
    "import.meta.env.VITE_COMMIT_SHA": JSON.stringify(getGitCommitSha()),
    "import.meta.env.VITE_WEAVE_PROFILE": JSON.stringify(
      process.env.WEAVE_PROFILE || "default"
    ),
  },
  server: {
    port: 3001,
    // Dev proxy: forward /api/* to the Go backend (replaces src/proxy.ts)
    proxy: {
      "/api": {
        target: "http://localhost:5001",
        changeOrigin: true,
      },
      "/ws": {
        target: "ws://localhost:5001",
        ws: true,
      },
    },
  },
  build: {
    outDir: "dist",
    emptyOutDir: true,
  },
});
