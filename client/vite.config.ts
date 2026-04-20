import { execSync } from "child_process";
import { resolve } from "path";
import vueJsx from "@vitejs/plugin-vue-jsx";
import { TanStackRouterVite } from "@tanstack/router-plugin/vite";
import { defineConfig } from "vite";
import vue from "@vitejs/plugin-vue";
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
  plugins: [
    TanStackRouterVite({
      target: "vue",
      autoCodeSplitting: true,
    }),
    vue(),
    vueJsx(),
  ],
  resolve: {
    alias: {
      "@": resolve(__dirname, "./src"),
    },
  },
  define: {
    "import.meta.env.VITE_APP_VERSION": JSON.stringify(getAppVersion()),
    "import.meta.env.VITE_COMMIT_SHA": JSON.stringify(getGitCommitSha()),
    "import.meta.env.VITE_WEAVE_PROFILE": JSON.stringify(process.env.WEAVE_PROFILE || "default"),
  },
  server: {
    port: 3002,
    proxy: {
      "/api": {
        target: "http://localhost:5001",
        changeOrigin: true,
      },
      "/ws": {
        target: "ws://localhost:5001",
        ws: true,
      },
      "/auth": {
        target: "http://localhost:5001",
        changeOrigin: true,
      },
    },
  },
  build: {
    outDir: "dist",
    emptyOutDir: true,
  },
});
