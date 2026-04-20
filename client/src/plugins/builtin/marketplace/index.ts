import type { FleetPluginManifest } from "@/plugins/types";
import MarketplacePanel from "./MarketplacePanel.vue";

export const marketplacePluginManifest = {
  descriptor: {
    id: "marketplace",
    displayName: "Marketplace",
    trustLevel: "built-in",
    hasFrontend: true,
    hasBackend: false,
  },
  contributions: {
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
