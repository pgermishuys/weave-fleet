import { resolve } from "path";
import { defineConfig } from "vite";

/**
 * Builds the service worker on its own: one IIFE file at dist/sw.js, unhashed, so the browser finds it at the
 * same URL every release. Runs after the app build (`emptyOutDir: false` keeps the app). No Workbox, no precache.
 */
export default defineConfig({
  resolve: {
    alias: {
      "@": resolve(__dirname, "./src"),
    },
  },
  publicDir: false,
  build: {
    outDir: "dist",
    emptyOutDir: false,
    copyPublicDir: false,
    sourcemap: false,
    lib: {
      entry: resolve(__dirname, "src/sw/sw.ts"),
      name: "FleetServiceWorker",
      formats: ["iife"],
      fileName: () => "sw.js",
    },
  },
});
