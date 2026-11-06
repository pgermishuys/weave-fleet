import { createFileRoute } from "@tanstack/vue-router";
import SettingsRoute from "@/components/settings/SettingsRoute.vue";

export const Route = createFileRoute("/settings_/plugins/$pluginId")({
  component: SettingsRoute,
});
