import { Boxes } from "lucide-vue-next";
import type { FleetPluginManifest } from "@/plugins/types";
import DockerPanel from "./DockerPanel.vue";

export const dockerPluginManifest = {
  descriptor: {
    id: "docker",
    displayName: "Docker",
    trustLevel: "built-in",
    hasFrontend: true,
    hasBackend: false,
  },
  contributions: {
    sidebarItems: [
      {
        viewId: "docker",
        label: "Docker",
        icon: Boxes,
        defaultPath: "/docker",
        order: 30,
      },
    ],
    sidebarPanels: [
      {
        viewId: "docker",
        component: DockerPanel,
        order: 30,
      },
    ],
  },
} as const satisfies FleetPluginManifest;

export default dockerPluginManifest;
