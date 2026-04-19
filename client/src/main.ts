import { createApp } from "vue";
import { createPinia } from "pinia";
import App from "./App.vue";
import "./assets/main.css";
import dockerPluginManifest from "@/plugins/builtin/docker";
import githubPluginManifest from "@/plugins/builtin/github";
import linearPluginManifest from "@/plugins/builtin/linear";
import marketplacePluginManifest from "@/plugins/builtin/marketplace";
import sentryPluginManifest from "@/plugins/builtin/sentry";
import slackPluginManifest from "@/plugins/builtin/slack";
import { usePluginRuntime } from "@/plugins/composable";
import { useThemeStore } from "@/stores/theme";
import { router } from "./router";

const app = createApp(App);
const pluginRuntime = usePluginRuntime();

pluginRuntime.registerPlugins([
  githubPluginManifest,
  linearPluginManifest,
  slackPluginManifest,
  dockerPluginManifest,
  sentryPluginManifest,
  marketplacePluginManifest,
]);

const pinia = createPinia();

app.use(pinia);

useThemeStore(pinia).initializeTheme();

await router.load();

app.mount("#app");
