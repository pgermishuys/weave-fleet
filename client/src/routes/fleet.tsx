import { createFileRoute } from "@tanstack/vue-router";
import MissionControlPage from "@/components/pages/MissionControlPage.vue";

export const Route = createFileRoute("/fleet")({
  component: MissionControlPage,
});
