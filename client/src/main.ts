import { createApp } from "vue";
import { createPinia } from "pinia";
import "@fontsource-variable/dm-sans";
import "@fontsource-variable/inter";
import "@fontsource-variable/jetbrains-mono";
import App from "./App.vue";
import "./assets/main.css";
import githubPluginManifest from "@/plugins/builtin/github";
import marketplacePluginManifest from "@/plugins/builtin/marketplace";
import "@/plugins/builtin/smart-links";
import { usePluginRuntime } from "@/plugins/composable";
import { useThemeStore } from "@/stores/theme";
import { useWorkspaceUiStore } from "@/stores/workspace-ui";
import { router } from "./router";

const app = createApp(App);
const pluginRuntime = usePluginRuntime();

pluginRuntime.registerPlugins([
  githubPluginManifest,
  marketplacePluginManifest,
]);

const pinia = createPinia();

app.use(pinia);

useThemeStore(pinia).initializeTheme();

const browserWindow = typeof window === "undefined"
  ? null
  : window as Window & typeof globalThis & {
    __WEAVE_TEST_API?: {
      getInlineToolDiffs: () => boolean;
      setInlineToolDiffs: (enabled: boolean) => void;
    };
  };

if (browserWindow) {
  browserWindow.__WEAVE_TEST_API = {
    getInlineToolDiffs: () => useWorkspaceUiStore(pinia).inlineToolDiffs,
    setInlineToolDiffs: (enabled: boolean) => {
      useWorkspaceUiStore(pinia).setInlineToolDiffs(enabled);
    },
  };
}

await router.load();

app.mount("#app");
