import { createFileRoute } from "@tanstack/vue-router";
import SettingsPage from "@/components/settings/SettingsPage.vue";

export const Route = createFileRoute("/settings")({
  component: SettingsPage,
});
