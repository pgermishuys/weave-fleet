import { createFileRoute } from "@tanstack/vue-router";
import SettingsRoute from "@/components/settings/SettingsRoute.vue";

export const Route = createFileRoute("/settings")({
  component: SettingsRoute,
});
