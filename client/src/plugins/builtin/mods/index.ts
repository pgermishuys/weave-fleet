import { WandSparkles } from "lucide-vue-next";
import ModsPluginPage from "./ModsPluginPage.vue";
import type { FleetPluginManifest } from "@/plugins/types";
import { modsOwnSwitch } from "./mods-switch";

export const modsPluginManifest = {
  descriptor: {
    id: "mods",
    displayName: "Mods",
    trustLevel: "built-in",
    hasFrontend: true,
    hasBackend: true,
    description: "Agents write small add-ons for Fleet; you review each one.",
  },
  ownSwitch: modsOwnSwitch,
  contributions: {
    configPage: {
      title: "Mods",
      component: ModsPluginPage,
      icon: WandSparkles,
    },
  },
} as const satisfies FleetPluginManifest;

export default modsPluginManifest;
