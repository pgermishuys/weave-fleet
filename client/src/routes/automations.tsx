import { createFileRoute } from "@tanstack/vue-router";
import AutomationsPage from "@/components/pages/AutomationsPage.vue";

export const Route = createFileRoute("/automations")({
  component: AutomationsPage,
});
