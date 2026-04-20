import { BadgeAlert } from "lucide-vue-next";
import type { FleetPluginManifest } from "@/plugins/types";
import SentryPanel from "./SentryPanel.vue";

export const sentryPluginManifest = {
  descriptor: {
    id: "sentry",
    displayName: "Sentry",
    trustLevel: "built-in",
    hasFrontend: true,
    hasBackend: false,
  },
  contributions: {
    sidebarItems: [
      {
        viewId: "sentry",
        label: "Sentry",
        icon: BadgeAlert,
        defaultPath: "/sentry",
        order: 40,
      },
    ],
    sidebarPanels: [
      {
        viewId: "sentry",
        component: SentryPanel,
        order: 40,
      },
    ],
  },
} as const satisfies FleetPluginManifest;

export default sentryPluginManifest;
