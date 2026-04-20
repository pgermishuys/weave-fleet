import { Workflow } from "lucide-vue-next";
import type { FleetPluginManifest } from "@/plugins/types";
import LinearPanel from "./LinearPanel.vue";

export const linearPluginManifest = {
  descriptor: {
    id: "linear",
    displayName: "Linear",
    trustLevel: "built-in",
    hasFrontend: true,
    hasBackend: false,
  },
  contributions: {
    sidebarItems: [
      {
        viewId: "linear",
        label: "Linear",
        icon: Workflow,
        defaultPath: "/linear",
        order: 10,
      },
    ],
    sidebarPanels: [
      {
        viewId: "linear",
        component: LinearPanel,
        order: 10,
      },
    ],
  },
} as const satisfies FleetPluginManifest;

export default linearPluginManifest;
