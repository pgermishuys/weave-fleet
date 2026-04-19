import { Github } from "lucide-vue-next";
import type { FleetPluginManifest } from "@/plugins/types";
import GitHubPanel from "./GitHubPanel.vue";
import GitHubSettings from "./GitHubSettings.vue";

export const githubPluginManifest = {
  descriptor: {
    id: "github",
    displayName: "GitHub",
    trustLevel: "built-in",
    hasFrontend: true,
    hasBackend: true,
  },
  contributions: {
    sidebarItems: [
      {
        viewId: "github",
        label: "GitHub",
        icon: Github,
        defaultPath: "/github",
        order: 100,
      },
    ],
    sidebarPanels: [
      {
        viewId: "github",
        component: GitHubPanel,
        order: 100,
      },
    ],
    configPage: {
      title: "GitHub integration",
      component: GitHubSettings,
      icon: Github,
    },
  },
} as const satisfies FleetPluginManifest;

export default githubPluginManifest;
