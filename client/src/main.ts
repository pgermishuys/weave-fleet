import { createApp } from "vue";
import { createPinia } from "pinia";
import "@fontsource-variable/dm-sans";
import "@fontsource-variable/inter";
import "@fontsource-variable/jetbrains-mono";
import App from "./App.vue";
import "./assets/main.css";
import githubPluginManifest from "@/plugins/builtin/github";
import marketplacePluginManifest from "@/plugins/builtin/marketplace";
import { usePluginRuntime } from "@/plugins/composable";
import { getPromptTrackingState } from "@/composables/use-send-prompt";
import { useSessionsStore } from "@/stores/sessions";
import { useThemeStore } from "@/stores/theme";
import { useWorkspaceUiStore } from "@/stores/workspace-ui";
import { installModsTestApi } from "@/lib/mods/test-api";
import { restoreActiveMachine } from "@/lib/machines";
import { startServiceWorker } from "@/composables/use-service-worker";
import { captureInstallPrompt } from "@/lib/phone/install-prompt";
import { router } from "./router";

// Decide which machine this page works in before anything asks a server for something.
restoreActiveMachine();

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
      getPromptState: () => unknown;
    };
  };

if (browserWindow) {
  browserWindow.__WEAVE_TEST_API = {
    getInlineToolDiffs: () => useWorkspaceUiStore(pinia).inlineToolDiffs,
    setInlineToolDiffs: (enabled: boolean) => {
      useWorkspaceUiStore(pinia).setInlineToolDiffs(enabled);
    },
    getPromptState: () => ({
      ...getPromptTrackingState(),
      sessions: useSessionsStore(pinia).sessions.map((session) => ({
        id: session.session.id,
        activityStatus: session.activityStatus,
        sessionStatus: session.sessionStatus,
        lifecycleStatus: session.lifecycleStatus,
      })),
    }),
  };
}

installModsTestApi(pinia);

await router.load();

// Chrome offers to install the app once, as the page loads; the phone setup screen shows it later.
captureInstallPrompt();

app.mount("#app");

// Notifications and installing to the Home Screen need the service worker; a tapped notification navigates here.
startServiceWorker((path) => {
  void router.history.push(path);
});
