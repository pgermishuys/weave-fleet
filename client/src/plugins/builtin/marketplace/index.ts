import { Puzzle } from "lucide-vue-next";
import type { FleetPluginManifest } from "@/plugins/types";

export const marketplacePluginManifest = {
  descriptor: {
    id: "marketplace",
    displayName: "Plugins",
    trustLevel: "built-in",
    hasFrontend: true,
    hasBackend: false,
    alwaysOn: true,
  },
  contributions: {
    sidebarItems: [
      {
        viewId: "marketplace",
        label: "Plugins",
        icon: Puzzle,
        // A shortcut: the rail opens Settings › Plugins, where plugins are listed and switched.
        defaultPath: "/settings/plugins",
        placement: "bottom",
        order: 0,
      },
    ],
  },
} as const satisfies FleetPluginManifest;

export default marketplacePluginManifest;
