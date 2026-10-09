import { Puzzle } from "lucide-vue-next";
import type { FleetPluginManifest } from "@/plugins/types";
import MarketplacePanel from "./MarketplacePanel.vue";

export const marketplacePluginManifest = {
  descriptor: {
    id: "marketplace",
    displayName: "Plugins",
    trustLevel: "built-in",
    hasFrontend: true,
    hasBackend: false,
  },
  contributions: {
    sidebarItems: [
      {
        viewId: "marketplace",
        label: "Plugins",
        icon: Puzzle,
        placement: "bottom",
        order: 0,
      },
    ],
    sidebarPanels: [
      {
        viewId: "marketplace",
        component: MarketplacePanel,
        order: 0,
      },
    ],
  },
} as const satisfies FleetPluginManifest;

export default marketplacePluginManifest;
