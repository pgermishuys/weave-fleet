import { Slack } from "lucide-vue-next";
import type { FleetPluginManifest } from "@/plugins/types";
import SlackPanel from "./SlackPanel.vue";

export const slackPluginManifest = {
  descriptor: {
    id: "slack",
    displayName: "Slack",
    trustLevel: "built-in",
    hasFrontend: true,
    hasBackend: false,
  },
  contributions: {
    sidebarItems: [
      {
        viewId: "slack",
        label: "Slack",
        icon: Slack,
        defaultPath: "/slack",
        order: 20,
      },
    ],
    sidebarPanels: [
      {
        viewId: "slack",
        component: SlackPanel,
        order: 20,
      },
    ],
  },
} as const satisfies FleetPluginManifest;

export default slackPluginManifest;
