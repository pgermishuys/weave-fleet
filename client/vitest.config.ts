import { fileURLToPath, URL } from "node:url";
import vue from "@vitejs/plugin-vue";
import vueJsx from "@vitejs/plugin-vue-jsx";
import { defineConfig } from "vitest/config";

export default defineConfig({
  plugins: [vue(), vueJsx()],
  resolve: {
    alias: {
      "@": fileURLToPath(new URL("./src", import.meta.url)),
    },
  },
  test: {
    environment: "jsdom",
    globals: true,
    setupFiles: ["./src/test-setup.ts"],
    coverage: {
      enabled: true,
      provider: "v8",
      reporter: ["text", "html"],
      include: [
        "src/composables/**/*.ts",
        "src/stores/**/*.ts",
        "src/lib/event-state.ts",
        "src/components/session/activity-stream-tool-card.ts",
      ],
      exclude: [
        "src/**/*.d.ts",
        "src/**/__tests__/**",
        "src/test-setup.ts",
      ],
    },
  },
});
