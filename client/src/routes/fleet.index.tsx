import { createFileRoute } from "@tanstack/vue-router";
import MissionControlPage from "@/components/pages/MissionControlPage.vue";

// Needs you. A machine is /fleet/machines/$machineId; the page reads which view from the address.
export const Route = createFileRoute("/fleet/")({
  component: MissionControlPage,
});
