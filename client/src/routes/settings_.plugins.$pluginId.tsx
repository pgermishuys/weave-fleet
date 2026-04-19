import { createFileRoute } from "@tanstack/vue-router";
import PluginConfigShell from "@/components/settings/PluginConfigShell.vue";

export const Route = createFileRoute("/settings_/plugins/$pluginId")({
  component: PluginConfigShell,
});
